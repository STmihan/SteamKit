/*
 * This file is subject to the terms and conditions defined in
 * file 'license.txt', which is part of this source code package.
 */


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using ProtoBuf;

namespace SteamKit2
{
    /// <summary>
    /// This handler is used for interacting with Steamworks unified messaging
    /// </summary>
    public partial class SteamUnifiedMessages : ClientMsgHandler
    {
        internal const string TrimmingMessageOfShame = "SteamUnifiedMessages needs to be rewritten in order to become compatible with trimming.";

        /// <summary>
        /// This wrapper is used for expression-based RPC calls using Steam Unified Messaging.
        /// </summary>
        public class UnifiedService<[DynamicallyAccessedMembers( Trimming.ForProtobufNet )] TService>
        {
            static readonly MethodInfo sendMessageMethod = typeof( SteamUnifiedMessages )
               .GetMethod( nameof( SteamUnifiedMessages.SendMessage ), BindingFlags.Public | BindingFlags.Instance )!;

            static readonly MethodInfo sendNotificationMethod = typeof( SteamUnifiedMessages )
                .GetMethod( nameof( SteamUnifiedMessages.SendNotification ), BindingFlags.Public | BindingFlags.Instance )!;

            internal UnifiedService( SteamUnifiedMessages steamUnifiedMessages )
            {
                this.steamUnifiedMessages = steamUnifiedMessages;
            }

            readonly SteamUnifiedMessages steamUnifiedMessages;

            /// <summary>
            /// Sends a message.
            /// Results are returned in a <see cref="ServiceMethodResponse"/>.
            /// The returned <see cref="AsyncJob{T}"/> can also be awaited to retrieve the callback result.
            /// </summary>
            /// <typeparam name="TResponse">The type of the protobuf object which is the response to the RPC call.</typeparam>
            /// <param name="expr">RPC call expression, e.g. x => x.SomeMethodCall(message);</param>
            /// <returns>The JobID of the request. This can be used to find the appropriate <see cref="ServiceMethodResponse"/>.</returns>
            [RequiresUnreferencedCode( TrimmingMessageOfShame )]
            public AsyncJob<ServiceMethodResponse> SendMessage<[DynamicallyAccessedMembers( Trimming.ForProtobufNet )] TResponse>( Expression<Func<TService, TResponse>> expr )
            {
                return SendMessageOrNotification( expr, false )!;
            }

            /// <summary>
            /// Sends a notification.
            /// </summary>
            /// <typeparam name="TResponse">The type of the protobuf object which is the response to the RPC call.</typeparam>
            /// <param name="expr">RPC call expression, e.g. x => x.SomeMethodCall(message);</param>
            [RequiresUnreferencedCode( TrimmingMessageOfShame )]
            public void SendNotification<TResponse>( Expression<Func<TService, TResponse>> expr )
            {
                SendMessageOrNotification( expr, true );
            }

            AsyncJob<ServiceMethodResponse>? SendMessageOrNotification<TResponse>( Expression<Func<TService, TResponse>> expr, bool isNotification )
            {
                ArgumentNullException.ThrowIfNull( expr );

                var call = ExtractMethodCallExpression( expr, nameof( expr ) );
                var methodInfo = call.Method;

                var argument = call.Arguments.Single();
                object message;

                if ( argument.NodeType == ExpressionType.MemberAccess )
                {
                    var unary = Expression.Convert( argument, typeof( object ) );
                    var lambda = Expression.Lambda<Func<object>>( unary );
                    var getter = lambda.Compile();
                    message = getter();
                }
                else
                {
                    throw new NotSupportedException( "Unknown Expression type" );
                }

                var rpcName = GetRpcName( methodInfo.Name );

                if ( isNotification )
                {
                    var notification = sendNotificationMethod.MakeGenericMethod( message.GetType() );
                    notification.Invoke( this.steamUnifiedMessages, [ rpcName, message ] );
                    return null;
                }

                var method = sendMessageMethod.MakeGenericMethod( message.GetType() );
                var result = method.Invoke( this.steamUnifiedMessages, [ rpcName, message ] )!;
                return ( AsyncJob<ServiceMethodResponse> )result;
            }

            static MethodCallExpression ExtractMethodCallExpression<TResponse>( Expression<Func<TService, TResponse>> expression, string paramName )
            {
                switch ( expression.NodeType )
                {
                    // Older code/tests/whatever were compiled down to just a single MethodCallExpression.
                    case ExpressionType.Call:
                        return ( MethodCallExpression )expression.Body;

                    // Newer code/tests/whatever are now compiled by wrapping the MethodCallExpression in a LambdaExpression.
                    case ExpressionType.Lambda:
                        if ( expression.Body.NodeType == ExpressionType.Call )
                        {
                            var lambda = ( LambdaExpression )expression;
                            return ( MethodCallExpression )lambda.Body;
                        }
                        break;
                }

                throw new ArgumentException( "Expression must be a method call.", paramName );
            }

            static string GetRpcName( string methodName ) => SteamUnifiedMessages.GetRpcName( typeof( TService ), methodName );
            static string GetRpcName( string methodName, int version ) => SteamUnifiedMessages.GetRpcName( typeof( TService ), methodName, version );
        }

        readonly ConcurrentDictionary<string, Type> services;
        Dictionary<EMsg, Action<IPacketMsg>> dispatchMap;

        [RequiresUnreferencedCode( TrimmingMessageOfShame )]
        internal SteamUnifiedMessages()
        {
            dispatchMap = new Dictionary<EMsg, Action<IPacketMsg>>
            {
                { EMsg.ServiceMethodResponse, HandleServiceMethodResponse },
                { EMsg.ServiceMethod, HandleServiceMethod },
            };

            services = new ConcurrentDictionary<string, Type>();
        }

        static string GetRpcName( Type serviceType, string methodName ) => GetRpcName( GetServiceName( serviceType ), methodName );
        static string GetRpcName( Type serviceType, string methodName, int version ) => GetRpcName( GetServiceName( serviceType ), methodName, version );

        static string GetServiceName( Type serviceType )
        {
            var serviceName = serviceType.Name;
            if ( serviceName.Length > 0 && serviceName[ 0 ] == 'I' )
            {
                serviceName = serviceName[ 1.. ];
            }
            return serviceName;
        }

        static string GetRpcName( string serviceName, string methodName ) => GetRpcName( serviceName, methodName, version: 1 );
        static string GetRpcName( string serviceName, string methodName, int version ) => string.Format( "{0}.{1}#{2}", serviceName, methodName, version );

        /// <summary>
        /// Sends a message.
        /// Results are returned in a <see cref="ServiceMethodResponse"/>.
        /// The returned <see cref="AsyncJob{T}"/> can also be awaited to retrieve the callback result.
        /// </summary>
        /// <typeparam name="TRequest">The type of a protobuf object.</typeparam>
        /// <param name="name">Name of the RPC endpoint. Takes the format ServiceName.RpcName</param>
        /// <param name="message">The message to send.</param>
        /// <returns>The JobID of the request. This can be used to find the appropriate <see cref="ServiceMethodResponse"/>.</returns>
        public AsyncJob<ServiceMethodResponse> SendMessage<[DynamicallyAccessedMembers( Trimming.ForProtobufNet )] TRequest>( string name, TRequest message )
            where TRequest : IExtensible, new()
        {
            if ( message == null )
            {
                throw new ArgumentNullException( nameof( message ) );
            }

            var eMsg = Client.SteamID == null ? EMsg.ServiceMethodCallFromClientNonAuthed : EMsg.ServiceMethodCallFromClient;
            var msg = new ClientMsgProtobuf<TRequest>( eMsg );
            msg.SourceJobID = Client.GetNextJobID();
            msg.Header.Proto.target_job_name = name;
            msg.Body = message;
            Client.Send( msg );

            return new AsyncJob<ServiceMethodResponse>( this.Client, msg.SourceJobID );
        }

        /// <summary>
        /// Sends a notification.
        /// </summary>
        /// <typeparam name="TRequest">The type of a protobuf object.</typeparam>
        /// <param name="name">Name of the RPC endpoint. Takes the format ServiceName.RpcName</param>
        /// <param name="message">The message to send.</param>
        public void SendNotification<[DynamicallyAccessedMembers( Trimming.ForProtobufNet )] TRequest>( string name, TRequest message )
            where TRequest : IExtensible, new()
        {
            if ( message == null )
            {
                throw new ArgumentNullException( nameof( message ) );
            }

            // Notifications do not set source jobid, otherwise Steam server will actively reject this message
            // if the method being used is a "Notification"
            var eMsg = Client.SteamID == null ? EMsg.ServiceMethodCallFromClientNonAuthed : EMsg.ServiceMethodCallFromClient;
            var msg = new ClientMsgProtobuf<TRequest>( eMsg );
            msg.Header.Proto.target_job_name = name;
            msg.Body = message;
            Client.Send( msg );
        }

        /// <summary>
        /// Creates a <see cref="UnifiedService&lt;TService&gt;"/> wrapper for expression-based unified messaging.
        /// </summary>
        /// <typeparam name="TService">The type of a service interface.</typeparam>
        /// <returns>The <see cref="UnifiedService&lt;TService&gt;"/> wrapper.</returns>
        public UnifiedService<TService> CreateService<[DynamicallyAccessedMembers( Trimming.ForProtobufNet )] TService>()
        {
            RegisterService<TService>();
            return new UnifiedService<TService>( this );
        }

        /// <summary>
        /// Registers a service type in order to recieve notifications. It is not neccesary to call this if
        /// <see cref="CreateService{TService}"/> has been called with the same TService parameter.
        /// </summary>
        /// <typeparam name="TService">The type of a service interface.</typeparam>
        public void RegisterService<[DynamicallyAccessedMembers( Trimming.ForProtobufNet )] TService>()
        {
            const string ServiceTypePrefix = "SteamKit2.Internal.I";
            var serviceType = typeof( TService );
            if ( serviceType.FullName is null || !serviceType.FullName.StartsWith( ServiceTypePrefix, StringComparison.Ordinal ) )
            {
                throw new InvalidOperationException( "Service type provided is not a generated SteamKit2 service interface." );
            }
            var serviceName = serviceType.FullName[ ServiceTypePrefix.Length.. ];
            services.TryAdd( serviceName, typeof( TService ) );
        }


        /// <summary>
        /// Handles a client message. This should not be called directly.
        /// </summary>
        /// <param name="packetMsg">The packet message that contains the data.</param>
        public override void HandleMsg( IPacketMsg packetMsg )
        {
            ArgumentNullException.ThrowIfNull( packetMsg );

            if ( !dispatchMap.TryGetValue( packetMsg.MsgType, out var handlerFunc ) )
            {
                // ignore messages that we don't have a handler function for
                return;
            }

            handlerFunc( packetMsg );
        }


        #region ClientMsg Handlers
        void HandleServiceMethodResponse( IPacketMsg packetMsg )
        {
            if ( packetMsg is not PacketClientMsgProtobuf packetMsgProto )
            {
                throw new InvalidDataException( "Packet message is expected to be protobuf." );
            }

            var callback = new ServiceMethodResponse( packetMsgProto );
            Client.PostCallback( callback );
        }

        [return: DynamicallyAccessedMembers( Trimming.ForProtobufNet )]
        Type? GetServiceInterfaceType( string serviceName )
            => services.TryGetValue( serviceName, out var serviceInterfaceType ) ? serviceInterfaceType : null;

        void HandleServiceMethod( IPacketMsg packetMsg )
        {
            if ( packetMsg is not PacketClientMsgProtobuf packetMsgProto )
            {
                throw new InvalidDataException( "Packet message is expected to be protobuf." );
            }

            var jobName = packetMsgProto.Header.Proto.target_job_name;
            if ( !string.IsNullOrEmpty( jobName ) )
            {
                var splitByDot = jobName.Split( '.' );
                var splitByHash = splitByDot[ 1 ].Split( '#' );

                var serviceName = splitByDot[ 0 ];
                var methodName = splitByHash[ 0 ];

                if ( GetServiceInterfaceType( serviceName ) is { } serviceInterfaceType )
                {
                    var method = serviceInterfaceType.GetMethod( methodName );
                    if ( method != null )
                    {
                        var argumentType = method.GetParameters().Single().ParameterType;

                        var callback = new ServiceMethodNotification( argumentType, packetMsg );
                        Client.PostCallback( callback );
                    }
                }
            }
        }
        #endregion
    }
}
