﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Handlers.Logging
{
    using System;
    using System.Net;
    using System.Text;
    using System.Threading.Tasks;
    using DotNetty.Buffers;
    using DotNetty.Common.Internal.Logging;
    using DotNetty.Transport.Channels;
    /// <summary>
    /// A <see cref="IChannelHandler"/> that logs all events using a logging framework.
    /// By default, all events are logged at <tt>DEBUG</tt> level.
    /// </summary>
    public class LoggingHandler : ChannelHandlerAdapter
    {
        private static readonly LogLevel DEFAULT_LEVEL = LogLevel.DEBUG;

        protected readonly IInternalLogger Logger;
        protected readonly InternalLogLevel InternalLevel;
        private readonly LogLevel level;


        /// <summary>
        /// Creates a new instance whose logger name is the fully qualified class
        /// name of the instance with hex dump enabled.
        /// </summary>
        public LoggingHandler()
            : this(DEFAULT_LEVEL)
        {
        }

        /// <summary>
        /// Creates a new instance whose logger name is the fully qualified class
        /// name of the instance
        /// </summary>
        /// <param name="level">the log level</param>
        public LoggingHandler(LogLevel level):this(typeof(LoggingHandler),level)
        {
        }
        /// <summary>
        /// Creates a new instance with the specified logger name and with hex dump
        /// enabled
        /// </summary>
        /// <param name="type">the class type to generate the logger for</param>
        public LoggingHandler(Type type)
            : this(type, DEFAULT_LEVEL)
        {
        }
        /// <summary>
        /// Creates a new instance with the specified logger name.
        /// </summary>
        /// <param name="type">the class type to generate the logger for</param>
        /// <param name="level">the log level</param>
        public LoggingHandler(Type type, LogLevel level)
        {
            if (type == null)
            {
                throw new NullReferenceException("type");
            }
            if (level == null)
            {
                throw new NullReferenceException("level");
            }

            Logger = InternalLoggerFactory.GetInstance(type);
            this.level = level;
            InternalLevel = level.ToInternalLevel();
        }

        /// <summary>
        /// Creates a new instance with the specified logger name using the default log level.
        /// </summary>
        /// <param name="name">the name of the class to use for the logger</param>
        public LoggingHandler(String name)
            : this(name, DEFAULT_LEVEL)
        {

        }
        /// <summary>
        /// Creates a new instance with the specified logger name.
        /// </summary>
        /// <param name="name">the name of the class to use for the logger</param>
        /// <param name="level">the log level</param>
        public LoggingHandler(String name, LogLevel level)
        {
            if (name == null)
            {
                throw new NullReferenceException("name");
            }
            if (level == null)
            {
                throw new NullReferenceException("level");
            }
            Logger = InternalLoggerFactory.GetInstance(name);
            this.level = level;
            InternalLevel = level.ToInternalLevel();
        }
        /// <summary>
        /// Returns the <see cref="LogLevel"/> that this handler uses to log
        /// </summary>
        public LogLevel Level {
            get { return level; }
        }

        public override void ChannelRegistered(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "REGISTERED"));
            }
            ctx.FireChannelRegistered();
        }

        public override void ChannelUnregistered(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "UNREGISTERED"));
            }
            ctx.FireChannelUnregistered();
        }

        public override void ChannelActive(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "ACTIVE"));
            }
            ctx.FireChannelActive();
        }

        public override void ChannelInactive(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "INACTIVE"));
            }
            ctx.FireChannelInactive();
        }

        public override void ExceptionCaught(IChannelHandlerContext ctx, Exception cause)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "EXCEPTION",cause),cause);
            }
            ctx.FireExceptionCaught(cause);
        }

        public override void UserEventTriggered(IChannelHandlerContext ctx, object evt)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "USER_EVENT", evt));
            }
            ctx.FireUserEventTriggered(evt);
        }

        public override Task BindAsync(IChannelHandlerContext ctx, EndPoint localAddress)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "BIND", localAddress));
            }
            return ctx.BindAsync(localAddress);
        }

        public override Task ConnectAsync(IChannelHandlerContext ctx, EndPoint remoteAddress, EndPoint localAddress)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "CONNECT", remoteAddress,localAddress));
            }
            return ctx.ConnectAsync(remoteAddress,localAddress);
        }

        public override Task DisconnectAsync(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "DISCONNECT"));
            }
            return ctx.DisconnectAsync();
        }

        public override Task CloseAsync(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "CLOSE"));
            }
            return ctx.CloseAsync();
        }

        public override Task DeregisterAsync(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "DEREGISTER"));
            }
            return ctx.DeregisterAsync();
        }

        public override void ChannelRead(IChannelHandlerContext ctx, object message)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "RECEIVED", message));
            }
            ctx.FireChannelRead(message);
        }

        public override Task WriteAsync(IChannelHandlerContext ctx, object msg)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "WRITE", msg));
            }
            return ctx.WriteAsync(msg);
        }

        public override void Flush(IChannelHandlerContext ctx)
        {
            if (Logger.IsEnabled(InternalLevel))
            {
                Logger.Log(InternalLevel, Format(ctx, "FLUSH"));
            }
            ctx.Flush();
        }

        /// <summary>
        /// Formats an event and returns the formatted message
        /// </summary>
        /// <param name="eventName">the name of the event</param>
        protected String Format(IChannelHandlerContext ctx, String eventName)
        {
            String chStr = ctx.Channel.ToString();
            return new StringBuilder(chStr.Length + 1 + eventName.Length)
                .Append(chStr)
                .Append(' ')
                .Append(eventName)
                .ToString();
        }

        /// <summary>
        /// Formats an event and returns the formatted message.
        /// </summary>
        /// <param name="eventName">the name of the event</param>
        /// <param name="arg">the argument of the event</param>
        protected String Format(IChannelHandlerContext ctx, String eventName, Object arg)
        {
            if (arg is IByteBuffer)
            {
                return FormatByteBuffer(ctx, eventName, (IByteBuffer)arg);
            }
            else if (arg is IByteBufferHolder)
            {
                return FormatByteBufferHolder(ctx, eventName, (IByteBufferHolder)arg);
            }
            else
            {
                return FormatSimple(ctx, eventName, arg);
            }

        }
        /// <summary>
        /// Formats an event and returns the formatted message.  This method is currently only used for formatting
        /// {@link ChannelHandler#connect(ChannelHandlerContext, SocketAddress, SocketAddress, ChannelPromise)}.
        /// </summary>
        /// <param name="eventName">the name of the event</param>
        /// <param name="firstArg">the first argument of the event</param>
        /// <param name="secondArg">the second argument of the event</param>
        protected String Format(IChannelHandlerContext ctx, String eventName, object firstArg, object secondArg)
        {
            if (secondArg == null)
            {
                return this.FormatSimple(ctx, eventName, firstArg);
            }
            String chStr = ctx.Channel.ToString();
            String arg1Str = firstArg.ToString();
            String arg2Str = secondArg.ToString();

            StringBuilder buf = new StringBuilder(
                 chStr.Length + 1 + eventName.Length + 2 + arg1Str.Length + 2 + arg2Str.Length );
            buf.Append(chStr).Append(' ').Append(eventName).Append(": ")
                .Append(arg1Str).Append(", ").Append(arg2Str);
            return buf.ToString();
        }
        /// <summary>
        /// Generates the default log message of the specified event whose argument is a  <see cref="IByteBuffer"/>.
        /// </summary>
        string FormatByteBuffer(IChannelHandlerContext ctx, string eventName, IByteBuffer msg)
        {
            String chStr = ctx.Channel.ToString();
            int length = msg.ReadableBytes;
            if (length == 0)
            {
                StringBuilder buf = new StringBuilder(chStr.Length + 1 + eventName.Length + 4);
                buf.Append(chStr).Append(' ').Append(eventName).Append(": 0B");
                return buf.ToString();
            }
            else
            {
                int rows = length / 16 + (length % 15 == 0 ? 0 : 1) + 4;
                StringBuilder buf = new StringBuilder(chStr.Length + 1 + eventName.Length + 2 + 10 + 1 + 2 + rows * 80);

                buf.Append(chStr).Append(' ').Append(eventName).Append(": ").Append(length).Append('B').Append('\n');
                ByteBufferUtil.AppendPrettyHexDump(buf,msg);

                return buf.ToString();
            }
        }
        /// <summary>
        /// Generates the default log message of the specified event whose argument is a <see cref="IByteBufferHolder"/>.
        /// </summary>
        string FormatByteBufferHolder(IChannelHandlerContext ctx, string eventName, IByteBufferHolder msg)
        {
            String chStr = ctx.Channel.ToString();
            String msgStr = msg.ToString();
            IByteBuffer content = msg.Content;
            int length = content.ReadableBytes;
            if (length == 0)
            {
                StringBuilder buf = new StringBuilder(chStr.Length + 1 + eventName.Length + 2 + msgStr.Length + 4);
                buf.Append(chStr).Append(' ').Append(eventName).Append(", ").Append(msgStr).Append(", 0B");
                return buf.ToString();
            }
            else
            {
                int rows = length / 16 + (length % 15 == 0 ? 0 : 1) + 4;
                StringBuilder buf = new StringBuilder(
                    chStr.Length + 1 + eventName.Length + 2 + msgStr.Length + 2 + 10 + 1 + 2 + rows * 80);

                buf.Append(chStr).Append(' ').Append(eventName).Append(": ")
                    .Append(msgStr).Append(", ").Append(length).Append('B').Append('\n');
                ByteBufferUtil.AppendPrettyHexDump(buf, content);

                return buf.ToString();
            }
        }

        /// <summary>
        /// Generates the default log message of the specified event whose argument is an arbitrary object.
        /// </summary>
        string FormatSimple(IChannelHandlerContext ctx, string eventName, object msg)
        {
            String chStr = ctx.Channel.ToString();
            String msgStr = msg.ToString();
            StringBuilder buf = new StringBuilder(chStr.Length + 1+ eventName.Length + 2 + msgStr.Length);
            return buf.Append(chStr).Append(' ').Append(eventName).Append(": ").Append(msgStr).ToString();
        }

    }
}
