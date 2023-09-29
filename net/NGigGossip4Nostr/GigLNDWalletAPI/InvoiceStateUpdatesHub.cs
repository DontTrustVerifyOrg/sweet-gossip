﻿using System;
using Microsoft.AspNetCore.SignalR;
using LNDWallet;
using Nito.AsyncEx;

namespace GigLNDWalletAPI;

public class InvoiceStateUpdatesHub : Hub
{
    AsyncMonitor asyncInvoiceMonitor;
    Queue<InvoiceStateChangedEventArgs> invoiceChangeQueue = new();
    public InvoiceStateUpdatesHub()
    {
        asyncInvoiceMonitor = new AsyncMonitor();
        Singlethon.LNDWalletManager.OnInvoiceStateChanged += LNDWalletManager_OnInvoiceStateChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if(disposing)
            Singlethon.LNDWalletManager.OnInvoiceStateChanged -= LNDWalletManager_OnInvoiceStateChanged;
        base.Dispose(disposing);
    }

    private void LNDWalletManager_OnInvoiceStateChanged(object sender, InvoiceStateChangedEventArgs e)
    {
        using (asyncInvoiceMonitor.Enter())
        {
            invoiceChangeQueue.Enqueue(e);
            asyncInvoiceMonitor.PulseAll();
        }
    }

    public override async Task OnConnectedAsync()
    {
        var authToken = Context?.GetHttpContext()?.Request.Query["authtoken"].First();
        var account = Singlethon.LNDWalletManager.ValidateAuthTokenAndGetAccount(authToken);
        Context.Items["publicKey"] = account.PublicKey;
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Singlethon.InvoiceHashes4ConnectionId.RemoveConnection((string)Context.Items["publicKey"]);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Monitor(string authToken, string paymentHash)
    {
        var account = Singlethon.LNDWalletManager.ValidateAuthTokenAndGetAccount(authToken);
        Singlethon.InvoiceHashes4ConnectionId.AddItem(account.PublicKey, paymentHash);
    }

    public async IAsyncEnumerable<string> Streaming(string authToken, CancellationToken cancellationToken)
    {
        var account = Singlethon.LNDWalletManager.ValidateAuthTokenAndGetAccount(authToken);
        while (true)
        {
            using (await asyncInvoiceMonitor.EnterAsync(cancellationToken))
            {
                await asyncInvoiceMonitor.WaitAsync(cancellationToken);
                while (invoiceChangeQueue.Count > 0)
                {
                    var ic = invoiceChangeQueue.Dequeue();
                    if (Singlethon.InvoiceHashes4ConnectionId.ContainsItem(account.PublicKey, ic.PaymentHash));
                        yield return ic.PaymentHash + "|" + ic.NewState.ToString();
                }
            }
        }
    }
}