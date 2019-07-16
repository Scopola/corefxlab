﻿// Licensed to the .NET Foundation under one or more agreements. 
// The .NET Foundation licenses this file to you under the MIT license. 
// See the LICENSE file in the project root for more information. 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace ALCProxy.Communication
{
    /// <summary>
    /// This currently is designed to only work in-process
    /// TODO: set up to allow for construction of out-of-proc proxies
    /// </summary>
    public abstract class ALCClient : IProxyClient
    {
        //Can't make this an IServerObject directly due to the type-loading barrier
        protected object _server;
        protected string _serverTypeName;
        protected Type _intType;
        protected MethodInfo _callMethod;
        public ALCClient(Type interfaceType, string serverName)
        {
            _intType = interfaceType;
            _serverTypeName = serverName;
        }
        private Type FindTypeInAssembly(string typeName, Assembly a)
        {
            //find the type we're looking for
            Type t = null;
            foreach (Type ty in a.GetTypes())
            {
                if (ty.Name.Equals(typeName) || (ty.Name.StartsWith(typeName) && ty.Name.Contains("`"))) // will happen for non-generic types, generics we need to find the additional "`1" afterwards
                {
                    t = ty;
                    break;
                }
            }
            if (t == null)
            {
                //no type asked for in the assembly
                throw new Exception("Proxy creation exception: No valid type while searching for the given type");
            }
            return t;
        }
        /// <summary>
        /// Creates the link between the client and the server, while also passing in all the information to the server for setup
        /// </summary>
        /// <param name="alc">The target AssemblyLoadContext</param>
        /// <param name="typeName">Name of the proxied type</param>
        /// <param name="assemblyPath">path of the assembly to the type</param>
        /// <param name="genericTypes">any generics that we need the proxy to work with</param>
        public void SetUpServer(AssemblyLoadContext alc, string typeName, string assemblyPath, object[] constructorParams, Type[] genericTypes)
        {
            Assembly a = alc.LoadFromAssemblyPath(assemblyPath);
            //find the type we're going to proxy inside the loaded assembly
            Type objType = FindTypeInAssembly(typeName, a);
            //Load *this* (The CommObjects) assembly into the ALC so we can get the server into the ALC
            Assembly aa = alc.LoadFromAssemblyPath(Assembly.GetAssembly(typeof(ClientDispatch)).CodeBase.Substring(8));
            Type serverType = FindTypeInAssembly(_serverTypeName, aa);
            //Set up all the generics to allow for the serverDispatch to be created correctly
            Type constructedType = serverType.MakeGenericType(_intType);
            //Give the client its reference to the server
            SerializeParameters(constructorParams, out IList<object> serializedConstArgs, out IList<Type> argTypes);
            _server = constructedType.GetConstructor(
                new Type[] { typeof(Type), typeof(Type[]), typeof(IList<object>), typeof(IList<Type>) })
                .Invoke(new object[] { objType, genericTypes, serializedConstArgs.ToList(), argTypes });
            _callMethod = _server.GetType().GetMethod("CallObject");
            //Attach to the unloading event
            alc.Unloading += UnloadClient;
        }
        private void UnloadClient(object sender)
        {
            _server = null; //unload only removes the reference to the proxy, doesn't do anything else, since the ALCs need to be cleaned up by the users before the GC can collect.
        }
        /// <summary>
        /// Converts each argument into a serialized version of the object so it can be sent over in a call-by-value fashion
        /// </summary>
        /// <param name="method">the methodInfo of the target method</param>
        /// <param name="args">the current objects assigned as arguments to send</param>
        /// <returns></returns>
        public object SendMethod(MethodInfo method, object[] args)
        {
            if (_server == null) //We've called the ALC unload, so the proxy has been cut off
            {
                throw new InvalidOperationException("Error in ALCClient: Proxy has been unloaded, or communication server was never set up correctly");
            }
            SerializeParameters(args, out IList<object> streams, out IList<Type> argTypes);
            object encryptedReturn = _callMethod.Invoke(_server, new object[] { method, streams, argTypes });
            return DeserializeReturnType(encryptedReturn, method.ReturnType);
        }
        protected void SerializeParameters(object[] arguments, out IList<object> serializedArgs, out IList<Type> argTypes)
        {
            argTypes = new List<Type>();
            serializedArgs = new List<object>();
            for (int i = 0; i < arguments.Length; i++)
            {
                object arg = arguments[i];
                Type t = arg.GetType();
                //Serialize the argument
                object serialArg = SerializeParameter(arg, t);
                serializedArgs.Add(serialArg);
                argTypes.Add(t);
            }
        }
        protected abstract object SerializeParameter(object param, Type paramType);
        protected abstract object DeserializeReturnType(object returnedObject, Type returnType);
    }
    public abstract class ALCServer<I> : IProxyServer
    {
        public object instance;
        public AssemblyLoadContext currentLoadContext;
        public ALCServer(Type instanceType, Type[] genericTypes, IList<object> serializedConstParams, IList<Type> constArgTypes)
        {
            currentLoadContext = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
            if (genericTypes != null)
            {
                instanceType = instanceType.MakeGenericType(genericTypes.Select(x => ConvertType(x)).ToArray());
            }
            var constructorParams = DeserializeParameters(serializedConstParams, constArgTypes);
            SetInstance(instanceType, constArgTypes.ToArray(), constructorParams);
        }
        /// <summary>
        /// Create the instance of the object we want to proxy
        /// </summary>
        /// <param name="instanceType">the type of the object we want</param>
        /// <param name="constructorTypes">The list of types that the constructor of the object takes in as an argument</param>
        /// <param name="constructorArgs">The physical objects that are the parameters to the constructor</param>
        protected void SetInstance(Type instanceType, Type[] constructorTypes, object[] constructorArgs)
        {
            var ci = instanceType.GetConstructor(constructorTypes);//GrabConstructor(constructorTypes, instanceType);//instanceType.GetConstructor(constructorTypes);
            instance = ci.Invoke(constructorArgs);
        }
        /// <summary>
        /// Takes a Type that's been passed from the user ALC, and loads it into the current ALC for use. 
        /// TODO: Figure out if we actually need to load here, or we can search the ALC for our loaded dependency which may already be loaded.
        /// </summary>
        protected Type ConvertType(Type toConvert)
        {
            string assemblyPath = Assembly.GetAssembly(toConvert).CodeBase.Substring(8);
            if (toConvert.IsPrimitive || assemblyPath.Contains("System.Private.CoreLib")) //Can't load/dont want to load extra types from System.Private.CoreLib
            {
                return toConvert;
            }
            //TODO can we look for the existence of the loaded assembly here, instead of just doing the load?
            return currentLoadContext.LoadFromAssemblyPath(assemblyPath).GetType(toConvert.FullName);
        }
        public object CallObject(MethodInfo targetMethod, IList<object> streams, IList<Type> argTypes)
        {
            //Turn the memstreams into their respective objects
            argTypes = argTypes.Select(x => ConvertType(x)).ToList();
            object[] args = DeserializeParameters(streams, argTypes);
            MethodInfo[] methods = instance.GetType().GetMethods();
            MethodInfo m = FindMethod(methods, targetMethod, argTypes.ToArray());
            if (m.ContainsGenericParameters)
            {
                //While this may work without the conversion, we want it to uphold the type-load boundary, don't let the passed in method use anything from outside the target ALC
                m = m.MakeGenericMethod(targetMethod.GetGenericArguments().Select(x => ConvertType(x)).ToArray());
            }
            return SerializeReturnObject(m.Invoke(instance, args), m.ReturnType);
        }
        /// <summary>
        /// Searches for methods within the type to find the one that matches our passed in type. Since the types are technically different,
        /// using a .Equals() on the methods doesn't have the comparison work correctly, so the first if statement does that manually for us.
        /// </summary>
        protected MethodInfo FindMethod(MethodInfo[] methods, MethodInfo targetMethod, Type[] parameterTypes/*These have already been converted so no issues with compatibility*/)
        {
            string methodName = targetMethod.Name;
            foreach (MethodInfo m in methods)
            {
                if (!m.Name.Equals(methodName) || parameterTypes.Length != m.GetParameters().Length)
                {
                    continue;
                }
                bool methodParamsAlligned = true;
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    if(!RecursivelyCheckForTypes(parameterTypes[i], m.GetParameters()[i].ParameterType))
                    {
                        methodParamsAlligned = false;
                        break;
                    }
                }
                if (!methodParamsAlligned)
                    continue;
                return m;
            }
            throw new MissingMethodException("Error in ALCProxy: Method Not found for " + instance.ToString() + ": " + methodName);
        }
        /// <summary>
        /// If a parameter of a function isn't the direct type that we've passed in, this function should find that the type we've passed is correct.
        /// </summary>
        private bool RecursivelyCheckForTypes(Type sentParameterType, Type toCompare)
        {
            Type t = sentParameterType;
            Type[] interfaces = t.GetInterfaces();
            if (sentParameterType.Equals(toCompare))
            {
                return true;
            }
            else if(t.BaseType == null && interfaces.Length == 0)
            {
                return false;
            }
            else
            {
                return RecursivelyCheckForTypes(t.BaseType, toCompare) || interfaces.Any(x => RecursivelyCheckForTypes(x, toCompare));
            }
        }
        /// <summary>
        /// Takes the serialized objects passed into the server and turns them into the specific objects we want, in the desired types we want
        /// </summary>
        /// <param name="streams"></param>
        /// <param name="argTypes"></param>
        /// <returns></returns>
        protected object[] DeserializeParameters(IList<object> streams, IList<Type> argTypes)
        {
            var convertedObjects = new List<object>();
            for (int i = 0; i < streams.Count; i++)
            {
                object s = streams[i];
                Type t = argTypes[i];
                object obj = DeserializeParameter(s, t);
                convertedObjects.Add(obj);
            }
            return convertedObjects.ToArray();
        }
        protected abstract object DeserializeParameter(object serializedParam, Type paramType);
        /// <summary>
        /// Once we've completed our method call to the real object, we need to convert the return type back into our type from the original ALC 
        /// the proxy is in, so we turn our returned object back into a stream that the client can decode
        /// </summary>
        /// <param name="returnedObject"></param>
        /// <param name="returnType"></param>
        /// <returns></returns>
        protected abstract object SerializeReturnObject(object returnedObject, Type returnType);
    }

    /// <summary>
    /// The client interface we can wrap both in-proc and out-of-proc proxies around, will add more methods here as they are found needed by both versions
    /// </summary>
    public interface IProxyServer
    {
        /// <summary>
        /// Sends a message to the server to proc the method call, and return the result
        /// </summary>
        /// <param name="method">the method that needs to be called</param>
        /// <param name="streams">The parameters for the given method, converted into a serialized object by the client that now need to be deserialized</param>
        /// <param name="types">The types of each stream, so the server knows how to decode the streams</param>
        /// <returns></returns>
        object CallObject(MethodInfo method, IList<object> streams, IList<Type> types);
    }
    /// <summary>
    /// The server interface we can wrap both in-proc and out-of-proc proxies around, will add more methods here as they are found needed by both versions
    /// </summary>
    public interface IProxyClient
    {
        /// <summary>
        /// Sends a message to the server to proc the method call, and return the result
        /// </summary>
        /// <param name="method">The method information of what needs to be called</param>
        /// <param name="args"></param>
        /// <returns>Whatever the target Method returns. We need to make sure that whatever gets returned is not of a type that is in our target ALC</returns>
        object SendMethod(MethodInfo method, object[] args);
        /// <summary>
        /// Creates the link between the client and the server, while also passing in all the information to the server for setup
        /// </summary>
        /// <param name="alc">The target AssemblyLoadContext</param>
        /// <param name="typeName">Name of the proxied type</param>
        /// <param name="assemblyPath">path of the assembly to the type</param>
        /// <param name="genericTypes">any generics that we need the proxy to work with</param>
        void SetUpServer(AssemblyLoadContext alc, string typeName, string assemblyPath, object[] constructorParams, Type[] genericTypes);

    }
}