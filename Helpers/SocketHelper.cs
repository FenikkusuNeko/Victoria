﻿using System;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.IO;
using System.Text;
using Victoria.Entities.Payloads;
using Newtonsoft.Json;

namespace Victoria.Helpers
{
    internal sealed class SocketHelper
    {
        private bool isUseable;
        private TimeSpan interval;
        private int reconnectAttempts;
        private ClientWebSocket clientWebSocket;
        private readonly Encoding _encoding;
        private readonly Configuration _config;
        private CancellationTokenSource cancellationTokenSource;

        public event Func<string, bool> OnMessage;

        public SocketHelper()
        {
            _encoding = new UTF8Encoding(false);
            ServicePointManager.ServerCertificateValidationCallback += (_, __, ___, ____) => true;
        }

        public async Task ConnectAsync()
        {
            cancellationTokenSource = new CancellationTokenSource();

            clientWebSocket = new ClientWebSocket();

            clientWebSocket.Options.SetRequestHeader("User-Id", $"{_config.UserId}");
            clientWebSocket.Options.SetRequestHeader("Num-Shards", $"{_config.Shards}");
            clientWebSocket.Options.SetRequestHeader("Authorization", _config.Password);
            var url = new Uri($"ws://{_config.Host}:{_config.Port}");
            try
            {
                await clientWebSocket.ConnectAsync(url, CancellationToken.None).ContinueWith(VerifyConnectionAsync);
            }
            catch
            {
                // Ignore all websocket exceptions.
            }
        }

        public Task SendPayloadAsync(BasePayload payload)
        {
            if (!isUseable)
                return Task.CompletedTask;

            var serialize = JsonConvert.SerializeObject(payload);
            //TODO: Log
            var seg = new ArraySegment<byte>(_encoding.GetBytes(serialize));
            return clientWebSocket.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task VerifyConnectionAsync(Task task)
        {
            if (task.IsCanceled || task.IsFaulted || task.Exception != null)
            {
                isUseable = false;
                await Task.Delay(interval).ContinueWith(_ => ConnectAsync()).ConfigureAwait(false);
            }
            else
            {
                isUseable = true;
                reconnectAttempts = 0;
                await ReceiveAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }

        private async Task RetryConnectionAsync()
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch
            {
                // Ignore
            }

            if (reconnectAttempts == _config.ReconnectAttempts)
                return;

            if (reconnectAttempts > _config.ReconnectAttempts && _config.ReconnectAttempts != -1)
                return;

            if (isUseable)
                return;

            reconnectAttempts++;
            interval += _config.ReconnectInterval;
            await Task.Delay(interval).ContinueWith(_ => ConnectAsync()).ConfigureAwait(false);
        }

        private async Task ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] bytes;
                    using (var stream = new MemoryStream())
                    {
                        var buffer = new byte[_config.BufferSize];
                        var segment = new ArraySegment<byte>(buffer);
                        while (clientWebSocket.State == WebSocketState.Open)
                        {
                            var result = await clientWebSocket.ReceiveAsync(segment, cancellationToken)
                                .ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                                if (result.CloseStatus == WebSocketCloseStatus.EndpointUnavailable)
                                {
                                    isUseable = false;
                                    await RetryConnectionAsync().ConfigureAwait(false);
                                    break;
                                }

                            stream.Write(buffer, 0, result.Count);
                            if (result.EndOfMessage)
                                break;
                        }

                        bytes = stream.ToArray();
                    }

                    if (bytes.Length <= 0)
                        continue;

                    var parse = _encoding.GetString(bytes).Trim('\0');
                    OnMessage(parse);
                }
            }
            catch (Exception ex) when (ex.HResult == -2147467259)
            {
                isUseable = false;
                await RetryConnectionAsync().ConfigureAwait(false);
            }
        }
    }
}