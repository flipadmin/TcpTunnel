﻿using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using TcpTunnel.Utils;

namespace TcpTunnel.Networking;

internal class TcpConnection : Connection
{
    private readonly Func<CancellationToken, ValueTask>? connectHandler;

    private readonly Action? closeHandler;

    private readonly Func<NetworkStream, CancellationToken, ValueTask<Stream?>>? streamModifier;

    private readonly Socket socket;

    private Stream? stream;

    private byte[]? currentReadBufferFromPool;

    public TcpConnection(
        Socket socket,
        bool useSendQueue,
        bool usePingTimer,
        Func<CancellationToken, ValueTask>? connectHandler = null,
        Action? closeHandler = null,
        Func<NetworkStream, CancellationToken, ValueTask<Stream?>>? streamModifier = null)
        : base(useSendQueue, usePingTimer)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.connectHandler = connectHandler;
        this.closeHandler = closeHandler;
        this.streamModifier = streamModifier;
    }

    protected Stream? Stream
    {
        get => this.stream;
    }

    /// <summary>
    /// Reads at least one byte (returning as early as possible) up to the specified
    /// <paramref name="maxLength"/> from the stream, except when the end of stream has
    /// been reached or <paramref name="maxLength"/> is 0.
    /// </summary>
    /// <param name="maxLength"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async ValueTask<ReceivedMessage?> ReceiveMessageAsync(
        int maxLength,
        CancellationToken cancellationToken)
    {
        if (this.currentReadBufferFromPool is not null)
        {
            ArrayPool<byte>.Shared.Return(this.currentReadBufferFromPool, clearArray: true);
            this.currentReadBufferFromPool = null;
        }

        try
        {
            // Wait until data is available.
            _ = await this.stream!.ReadAsync(Memory<byte>.Empty, cancellationToken)
                .ConfigureAwait(false);

            // Get a receive buffer from the pool.
            int maxReceiveCount = maxLength is -1 ?
                Constants.ReceiveBufferSize :
                Math.Min(Constants.ReceiveBufferSize, maxLength);

            this.currentReadBufferFromPool = ArrayPool<byte>.Shared.Rent(maxReceiveCount);

            int count = await this.stream.ReadAsync(
                this.currentReadBufferFromPool.AsMemory()
                    [..(maxLength is -1 ? this.currentReadBufferFromPool.Length : maxReceiveCount)],
                cancellationToken)
                .ConfigureAwait(false);

            if (count > 0)
            {
                var bufferMemory = this.currentReadBufferFromPool.AsMemory()[..count];
                return new ReceivedMessage(bufferMemory, ReceivedMessageType.Unknown);
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            // Ensure that a thread switch happens in case the current continuation is
            // called inline from CancellationTokenSource.Cancel(), which could lead to
            // deadlocks in certain situations (e.g. when holding some lock).
            await Task.Yield();
            throw;
        }
    }

    protected override async ValueTask HandleInitializationAsync(CancellationToken cancellationToken)
    {
        await base.HandleInitializationAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.connectHandler is { } connectHandler)
                await connectHandler(cancellationToken).ConfigureAwait(false);

            var ns = new NetworkStream(this.socket, ownsSocket: false);
            this.stream = ns;

            if (this.streamModifier is not null)
            {
                var newStream = await this.streamModifier(ns, cancellationToken)
                    .ConfigureAwait(false);

                if (newStream is not null)
                    this.stream = newStream;
            }
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            // Ensure that a thread switch happens in case the current continuation is
            // called inline from CancellationTokenSource.Cancel(), which could lead to
            // deadlocks in certain situations (e.g. when holding some lock).
            await Task.Yield();
            throw;
        }
    }

    protected override async ValueTask HandleCloseAsync()
    {
        if (this.currentReadBufferFromPool is not null)
        {
            ArrayPool<byte>.Shared.Return(this.currentReadBufferFromPool, clearArray: true);
            this.currentReadBufferFromPool = null;
        }

        await base.HandleCloseAsync().ConfigureAwait(false);

        // Dispose the stream.
        // Note: Disposing the Socket itself should be done by the caller
        // because they passed the instance to us.
        if (this.stream is not null)
            await this.stream.DisposeAsync().ConfigureAwait(false);

        this.closeHandler?.Invoke();
    }

    protected override ValueTask CloseCoreAsync(bool normalClose, CancellationToken cancellationToken)
    {
        if (normalClose)
        {
            // Shutdown the send channel.
            this.socket.Shutdown(SocketShutdown.Send);
        }
        else
        {
            // Close the socket with a timeout of 0, so that it resets
            // the connection.
            this.socket.Close(0);
        }

        return default;
    }

    protected override async ValueTask SendMessageCoreAsync(
            Memory<byte> message,
            bool textMessage,
            CancellationToken cancellationToken)
    {
        // We only support binary messages.
        if (textMessage)
        {
            throw new ArgumentException(
                $"Only binary messages are supported with the {nameof(TcpConnection)}.");
        }

        try
        {
            await this.stream!.WriteAsync(message, cancellationToken)
                .ConfigureAwait(false);

            await this.stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.CanCatch())
        {
            // Ensure that a thread switch happens in case the current continuation is
            // called inline from CancellationTokenSource.Cancel(), which could lead to
            // deadlocks in certain situations (e.g. when holding some lock).
            await Task.Yield();
            throw;
        }
    }
}