﻿using System;
using System.Diagnostics;
using System.Net.WebSockets;
using GigDebugLoggerAPIClient;
using GigLNDWalletAPIClient;
using NetworkClientToolkit;

namespace NGigGossip4Nostr;

public interface IPaymentStatusUpdatesMonitorEvents
{
    public void OnPaymentStatusChange(string status, byte[] data);
}

public class PaymentStatusUpdatesMonitor : HubMonitor
{
    GigGossipNode gigGossipNode;
    public IPaymentStatusUpdatesClient PaymentStatusUpdatesClient;
    CancellationTokenSource CancellationTokenSource = new();

    private readonly LogWrapper<PaymentStatusUpdatesMonitor> TRACE = FlowLoggerFactory.Trace<PaymentStatusUpdatesMonitor>();

    public PaymentStatusUpdatesMonitor(GigGossipNode gigGossipNode)
    {
        this.gigGossipNode = gigGossipNode;
    }

    public bool IsPaymentMonitored(string phash)
    {
        using var TL = TRACE.Log().Args(phash);
        try
        {
            return TL.Ret(
                 (from i in gigGossipNode.nodeContext.Value.MonitoredPayments
                    where i.PaymentHash == phash && i.PublicKey == this.gigGossipNode.PublicKey
                    select i).FirstOrDefault() != null
            );
        }
        catch (Exception ex)
        {
            TL.Exception(ex);
            throw;
        }
     }

    public async Task MonitorPaymentAsync(string phash, byte[] data)
    {
        using var TL = TRACE.Log().Args(phash,data);
        try
        {
            if ((from i in gigGossipNode.nodeContext.Value.MonitoredPayments
                where i.PaymentHash == phash && i.PublicKey == this.gigGossipNode.PublicKey
                select i).FirstOrDefault() != null)
            {
                TL.Warning("Payment already monitored");
                return;
            }

            var obj = new MonitoredPaymentRow()
            {
                PublicKey = this.gigGossipNode.PublicKey,
                PaymentHash = phash,
                PaymentStatus = "Unknown",
                Data = data,
            };
            gigGossipNode.nodeContext.Value.AddObject(obj);

            try
            {
                await this.PaymentStatusUpdatesClient.MonitorAsync(await gigGossipNode.MakeWalletAuthToken(), phash, CancellationTokenSource.Token);
            }
            catch
            {
                gigGossipNode.nodeContext.Value.RemoveObject(obj);
                throw;
            }
        }
        catch (Exception ex)
        {
            TL.Exception(ex);
            throw;
        }
    }

    public async Task StopPaymentMonitoringAsync(string phash)
    {
        using var TL = TRACE.Log().Args(phash);
        try
        {
            var o = (from i in gigGossipNode.nodeContext.Value.MonitoredPayments
                    where i.PaymentHash == phash && i.PublicKey == this.gigGossipNode.PublicKey
                    select i).FirstOrDefault();
            if (o == null)
            {
                TL.Warning("Payment not monitored");
                return;
            }

            await this.PaymentStatusUpdatesClient.StopMonitoringAsync(await gigGossipNode.MakeWalletAuthToken(), phash, CancellationTokenSource.Token);
            gigGossipNode.nodeContext.Value.RemoveObject(o);
        }
        catch (Exception ex)
        {
            TL.Exception(ex);
            throw;
        }
    }


    public async Task StartAsync()
    {
        using var TL = TRACE.Log().Args();
        try
        {
            this.OnServerConnectionState += PaymentStatusUpdatesMonitor_OnServerConnectionState;
            PaymentStatusUpdatesClient = gigGossipNode.GetWalletClient().CreatePaymentStatusUpdatesClient();

            await base.StartAsync(
                async () =>
                {
                    var token = await gigGossipNode.MakeWalletAuthToken();
                    await PaymentStatusUpdatesClient.ConnectAsync(token, CancellationTokenSource.Token);
                },
                async () =>
                {
                    {
                        var payToMon = (from i in gigGossipNode.nodeContext.Value.MonitoredPayments
                                        where i.PublicKey == this.gigGossipNode.PublicKey
                                        && i.PaymentStatus != "Succeeded" && i.PaymentStatus != "Failed"
                                        select i).ToList();

                        foreach (var pay in payToMon)
                        {
                            TL.Iteration(pay);
                            try
                            {
                                var status = WalletAPIResult.Get<string>(await gigGossipNode.GetWalletClient().GetPaymentStatusAsync(await gigGossipNode.MakeWalletAuthToken(), pay.PaymentHash, CancellationTokenSource.Token));
                                if (status != pay.PaymentStatus)
                                {
                                    TL.Info("OnPaymentStatusChange");
                                    gigGossipNode.OnPaymentStatusChange(status, pay.Data);
                                    pay.PaymentStatus = status;
                                    gigGossipNode.nodeContext.Value.SaveObject(pay);
                                }
                            }
                            catch (GigLNDWalletAPIException ex)
                            {
                                TL.Exception(ex);
                            }
                        }
                    }

                    await foreach (var paystateupd in this.PaymentStatusUpdatesClient.StreamAsync(await this.gigGossipNode.MakeWalletAuthToken(), CancellationTokenSource.Token))
                    {
                        TL.Iteration(paystateupd);
                        var invp = paystateupd.Split('|');
                        var payhash = invp[0];
                        var status = invp[1];
                        var pay = (from i in gigGossipNode.nodeContext.Value.MonitoredPayments
                                where i.PublicKey == this.gigGossipNode.PublicKey
                                && i.PaymentStatus != "Succeeded" && i.PaymentStatus != "Failed"
                                && i.PaymentHash == payhash
                                select i).FirstOrDefault();
                        if (pay != null)
                        {
                            if (status != pay.PaymentStatus)
                            {
                                TL.Info("OnPaymentStatusChange");
                                gigGossipNode.OnPaymentStatusChange(status, pay.Data);
                                pay.PaymentStatus = status;
                                gigGossipNode.nodeContext.Value.SaveObject(pay);
                            }
                        }
                        else
                        {
                            TL.Warning("Payment not monitored");
                        }
                    }
                },
                PaymentStatusUpdatesClient.Uri,
                gigGossipNode.RetryPolicy,
                CancellationTokenSource.Token
            );
        }
        catch (Exception ex)
        {
            TL.Exception(ex);
            throw;
        }
    }

    private void PaymentStatusUpdatesMonitor_OnServerConnectionState(object? sender, ServerConnectionStateEventArgs e)
    {
        using var TL = TRACE.Log().Args(sender,e);
        try
        {
            gigGossipNode.FireOnServerConnectionState(ServerConnectionSource.WalletAPI, e.State, e.Uri);
        }
        catch (Exception ex)
        {
            TL.Exception(ex);
        }
    }

    public void Stop()
    {
        using var TL = TRACE.Log();
        try
        {
            OnServerConnectionState -= PaymentStatusUpdatesMonitor_OnServerConnectionState;
            base.Stop(CancellationTokenSource);
        }
        catch (Exception ex)
        {
            TL.Exception(ex);
            throw;
        }
    }
}

