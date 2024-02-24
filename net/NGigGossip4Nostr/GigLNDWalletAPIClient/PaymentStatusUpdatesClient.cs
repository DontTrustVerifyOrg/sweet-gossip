﻿using System;
using Microsoft.AspNetCore.SignalR.Client;
namespace GigLNDWalletAPIClient
{
    public interface IPaymentStatusUpdatesClient
    {
        Task ConnectAsync(string authToken, CancellationToken cancellationToken);
        Task MonitorAsync(string authToken, string paymentHash, CancellationToken cancellationToken);
        Task StopMonitoringAsync(string authToken, string paymentHash, CancellationToken cancellationToken);
        IAsyncEnumerable<string> StreamAsync(string authToken, CancellationToken cancellationToken);
    }

    public class PaymentStatusUpdatesClient : IPaymentStatusUpdatesClient
    {
        IWalletAPI swaggerClient;
        private readonly HttpMessageHandler? httpMessageHandler;
        HubConnection connection;
        SemaphoreSlim slimLock = new(1, 1);

        internal PaymentStatusUpdatesClient(IWalletAPI swaggerClient, HttpMessageHandler? httpMessageHandler = null)
        {
            this.swaggerClient = swaggerClient;
            this.httpMessageHandler = httpMessageHandler;
        }

        public async Task ConnectAsync(string authToken, CancellationToken cancellationToken)
        {
            await slimLock.WaitAsync();
            try
            {
                var builder = new HubConnectionBuilder();

                if (httpMessageHandler != null)
                    builder.WithUrl(swaggerClient.BaseUrl + "paymentstatusupdates?authtoken=" + Uri.EscapeDataString(authToken), (options) => { options.HttpMessageHandlerFactory = (messageHndl) => { return httpMessageHandler; }; });
                else
                    builder.WithUrl(swaggerClient.BaseUrl + "paymentstatusupdates?authtoken=" + Uri.EscapeDataString(authToken));

                connection = builder.Build();
                await connection.StartAsync(cancellationToken);
            }
            finally
            {
                slimLock.Release();
            }
        }

        public async Task MonitorAsync(string authToken, string paymentHash, CancellationToken cancellationToken)
        {
            await slimLock.WaitAsync();
            try
            {
                await connection.SendAsync("Monitor", authToken, paymentHash, cancellationToken);
            }
            finally
            {
                slimLock.Release();
            }
        }

        public async Task StopMonitoringAsync(string authToken, string paymentHash, CancellationToken cancellationToken)
        {
            await slimLock.WaitAsync();
            try
            {
                await connection.SendAsync("StopMonitoring", authToken, paymentHash, cancellationToken);
            }
            finally
            {
                slimLock.Release();
            }
        }

        public IAsyncEnumerable<string> StreamAsync(string authToken, CancellationToken cancellationToken)
        {
            slimLock.Wait();
            try
            {
                return connection.StreamAsync<string>("StreamAsync", authToken, cancellationToken);
            }
            finally
            {
                slimLock.Release();
            }
        }
    }
}

