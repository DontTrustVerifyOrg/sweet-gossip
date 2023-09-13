﻿using System;
using NGigGossip4Nostr;
using System.Diagnostics;
using System.Text;
using CryptoToolkit;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using NBitcoin.Secp256k1;
using NGigTaxiLib;
using System.Reflection;
using NGeoHash;
using NBitcoin.RPC;
using GigLNDWalletAPIClient;

namespace GigWorkerBasicTest;

public static class MainThreadControl
{
    public static bool IsRunning { get; set; } = true;
    public static object Ctrl = new object();
}

public class BasicTest
{
    string[] args;

    IConfigurationRoot GetConfigurationRoot(string defaultFolder, string iniName)
    {
        var basePath = Environment.GetEnvironmentVariable("GIGGOSSIP_BASEDIR");
        if (basePath == null)
            basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaultFolder);
        foreach (var arg in args)
            if (arg.StartsWith("--basedir"))
                basePath = arg.Substring(arg.IndexOf('=') + 1).Trim().Replace("\"", "").Replace("\'", "");

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(basePath)
               .AddIniFile(iniName)
               .AddEnvironmentVariables()
               .AddCommandLine(args);

        return builder.Build();
    }
    NodeSettings gigWorkerSettings, customerSettings;
    SettlerAdminSettings settlerAdminSettings;
    BitcoinSettings bitcoinSettings;
    ApplicationSettings applicationSettings;

    public BasicTest(string[] args)
    {
        this.args = args;
        var config = GetConfigurationRoot(".giggossip", "basictest.conf");
        gigWorkerSettings = config.GetSection("gigworker").Get<NodeSettings>();
        customerSettings = config.GetSection("customer").Get<NodeSettings>();
        settlerAdminSettings = config.GetSection("settleradmin").Get<SettlerAdminSettings>();
        bitcoinSettings = config.GetSection("bitcoin").Get<BitcoinSettings>();
        applicationSettings = config.GetSection("application").Get<ApplicationSettings>();
    }


    HttpClient httpClient = new HttpClient();

    public void Run()
    {
        FlowLogger.Start(applicationSettings.FlowLoggerPath.Replace("$HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        var bitcoinClient = bitcoinSettings.NewRPCClient();

        // load bitcoin node wallet
        RPCClient? bitcoinWalletClient;
        try
        {
            bitcoinWalletClient = bitcoinClient.LoadWallet(bitcoinSettings.WalletName); ;
        }
        catch (RPCException exception ) when (exception.RPCCode== RPCErrorCode.RPC_WALLET_ALREADY_LOADED)
        {
            bitcoinWalletClient = bitcoinClient.SetWalletContext(bitcoinSettings.WalletName);
        }

        bitcoinWalletClient.Generate(10); // generate some blocks


        var settlerPrivKey = settlerAdminSettings.PrivateKey.AsECPrivKey();
        var settlerPubKey = settlerPrivKey.CreateXOnlyPubKey();
        var settlerSelector = new SimpleSettlerSelector();
        var settlerClient = settlerSelector.GetSettlerClient(settlerAdminSettings.SettlerOpenApi);
        var gtok = settlerClient.GetTokenAsync(settlerPubKey.AsHex()).Result;
        var token = Crypto.MakeSignedTimedToken(settlerPrivKey, DateTime.Now, gtok);
        var val = Convert.ToBase64String(Encoding.Default.GetBytes("ok"));

        FlowLogger.SetupParticipantWithAutoAlias(Encoding.Default.GetBytes(settlerAdminSettings.SettlerOpenApi.AbsoluteUri).AsHex(), "settler", false);

        var gigWorker = new GigGossipNode(
            gigWorkerSettings.ConnectionString,
            gigWorkerSettings.PrivateKey.AsECPrivKey(),
            gigWorkerSettings.GetNostrRelays(),
            gigWorkerSettings.ChunkSize
            );

        FlowLogger.SetupParticipant(gigWorker.PublicKey, "GigWorker", true);

        settlerClient.GiveUserPropertyAsync(
                token, gigWorker.PublicKey,
                "drive", val,
                (DateTime.Now + TimeSpan.FromDays(1)).ToLongDateString()
             ).Wait();

        var gigWorkerCert = Crypto.DeserializeObject<Certificate>(
            settlerClient.IssueCertificateAsync(
                 token, gigWorker.PublicKey, new List<string> { "drive" }).Result);

        var customer = new GigGossipNode(
            customerSettings.ConnectionString,
            customerSettings.PrivateKey.AsECPrivKey(),
            customerSettings.GetNostrRelays(),
            customerSettings.ChunkSize
            );

        FlowLogger.SetupParticipant(customer.PublicKey, "Customer", true);

        settlerClient.GiveUserPropertyAsync(
            token, customer.PublicKey,
            "ride", val,
            (DateTime.Now + TimeSpan.FromDays(1)).ToLongDateString()
         ).Wait();

        var customerCert = Crypto.DeserializeObject<Certificate>(
             settlerClient.IssueCertificateAsync(
                token, customer.PublicKey, new List<string> { "ride" }).Result);


        gigWorker.Init(
            gigWorkerSettings.Fanout,
            gigWorkerSettings.PriceAmountForRouting,
            TimeSpan.FromMilliseconds(gigWorkerSettings.BroadcastConditionsTimeoutMs),
            gigWorkerSettings.BroadcastConditionsPowScheme,
            gigWorkerSettings.BroadcastConditionsPowComplexity,
            TimeSpan.FromMilliseconds(gigWorkerSettings.TimestampToleranceMs),
            TimeSpan.FromSeconds(gigWorkerSettings.InvoicePaymentTimeoutSec),
            gigWorkerSettings.GetLndWalletClient(httpClient));
        //await gigWorker.LoadCertificates(gigWorkerSettings.SettlerOpenApi);

        customer.Init(
            customerSettings.Fanout,
            customerSettings.PriceAmountForRouting,
            TimeSpan.FromMilliseconds(customerSettings.BroadcastConditionsTimeoutMs),
            customerSettings.BroadcastConditionsPowScheme,
            customerSettings.BroadcastConditionsPowComplexity,
            TimeSpan.FromMilliseconds(customerSettings.TimestampToleranceMs),
            TimeSpan.FromSeconds(customerSettings.InvoicePaymentTimeoutSec),
            customerSettings.GetLndWalletClient(httpClient));

        //await customer.LoadCertificates(customerSettings.SettlerOpenApi);

        var ballanceOfCustomer= customer.LNDWalletClient.GetBalanceAsync(customer.MakeWalletAuthToken()).Result;

        if (ballanceOfCustomer == 0)
        {
            var newBitcoinAddressOfCustomer =  customer.LNDWalletClient.NewAddressAsync(customer.MakeWalletAuthToken()).Result;
            Console.WriteLine(newBitcoinAddressOfCustomer);

            bitcoinClient.SendToAddress(NBitcoin.BitcoinAddress.Create(newBitcoinAddressOfCustomer, bitcoinSettings.GetNetwork()), new NBitcoin.Money(10000000ul));

            bitcoinClient.Generate(10); // generate some blocks

            do
            {
                if ( customer.LNDWalletClient.GetBalanceAsync(customer.MakeWalletAuthToken()).Result > 0)
                    break;
                Thread.Sleep(1000);
            } while (true);

            ballanceOfCustomer =  customer.LNDWalletClient.GetBalanceAsync(customer.MakeWalletAuthToken()).Result;
        }



        gigWorker.Start(new GigWorkerGossipNodeEvents(gigWorkerSettings.SettlerOpenApi, gigWorkerCert));
        gigWorker.ClearContacts();
        customer.Start(new CustomerGossipNodeEvents());
        customer.ClearContacts();

        gigWorker.AddContact(customer.PublicKey,"Customer" );
        customer.AddContact(gigWorker.PublicKey, "GigWorker");

        {
            var fromGh = GeoHash.Encode(latitude: 42.6, longitude: -5.6, numberOfChars: 7);
            var toGh = GeoHash.Encode(latitude: 42.5, longitude: -5.6, numberOfChars: 7);

            customer.BroadcastTopic(new TaxiTopic()
            {
                FromGeohash = fromGh,
                ToGeohash = toGh,
                PickupAfter = DateTime.Now,
                DropoffBefore = DateTime.Now.AddMinutes(20)
            },
            customerCert);

        }

        while (MainThreadControl.IsRunning)
        {
            lock (MainThreadControl.Ctrl)
                Monitor.Wait(MainThreadControl.Ctrl);
        }

        gigWorker.Stop();
        customer.Stop();

        FlowLogger.Stop();
    }
}

public class GigWorkerGossipNodeEvents : IGigGossipNodeEvents
{
    Uri settlerUri;
    Certificate selectedCertificate;
    public GigWorkerGossipNodeEvents(Uri settlerUri, Certificate selectedCertificate)
    {
        this.settlerUri = settlerUri;
        this.selectedCertificate = selectedCertificate;
    }

    public void OnAcceptBroadcast(GigGossipNode me, string peerPublicKey, POWBroadcastFrame broadcastFrame)
    {
        var taxiTopic = Crypto.DeserializeObject<TaxiTopic>(
            broadcastFrame.BroadcastPayload.SignedRequestPayload.Topic);

        if (taxiTopic != null)
        {
            me.AcceptBroadcast( peerPublicKey, broadcastFrame,
                new AcceptBroadcastResponse()
                {
                    Message = Encoding.Default.GetBytes(me.PublicKey),
                    Fee = 4321,
                    SettlerServiceUri = settlerUri,
                    MyCertificate = selectedCertificate
                });
            FlowLogger.NewEvent(me.PublicKey, "AcceptBraodcast");
        }
    }

    public void OnNetworkInvoiceAccepted(GigGossipNode me, InvoiceAcceptedData iac)
    {
        me.PayNetworkInvoice(iac);
    }

    public void OnInvoiceSettled(GigGossipNode me, Uri serviceUri, string paymentHash, string preimage)
    {
        FlowLogger.NewEvent(me.PublicKey, "InvoiceSettled");
        lock (MainThreadControl.Ctrl)
        {
            MainThreadControl.IsRunning = false;
            Monitor.PulseAll(MainThreadControl.Ctrl);
        }
    }

    public void OnNewResponse(GigGossipNode me, ReplyPayload replyPayload, string replyInvoice, PayReq decodedReplyInvoice, string networkInvoice, PayReq decodedNetworkInvoice)
    {
    }

    public void OnResponseReady(GigGossipNode me, ReplyPayload replyPayload, string key)
    {
    }

    public void OnPaymentStatusChange(GigGossipNode me, string status, PaymentData paydata)
    {
    }
}

public class CustomerGossipNodeEvents : IGigGossipNodeEvents
{
    public CustomerGossipNodeEvents()
    {
    }

    public void OnAcceptBroadcast(GigGossipNode me, string peerPublicKey, POWBroadcastFrame broadcastFrame)
    {
    }

    public void OnNetworkInvoiceAccepted(GigGossipNode me, InvoiceAcceptedData iac)
    {
    }

    public void OnInvoiceSettled(GigGossipNode me, Uri serviceUri, string paymentHash, string preimage)
    {
    }

    public void OnNewResponse(GigGossipNode me, ReplyPayload replyPayload, string replyInvoice, PayReq decodedReplyInvoice, string networkInvoice, PayReq decodedNetworkInvoice)
    {
        FlowLogger.NewEvent(me.PublicKey, "AcceptResponse");
        me.AcceptResponse(replyPayload, replyInvoice,decodedReplyInvoice,networkInvoice,decodedNetworkInvoice);
    }

    public void OnResponseReady(GigGossipNode me, ReplyPayload replyPayload, string key)
    {
        var message = Encoding.Default.GetString(Crypto.SymmetricDecrypt<byte[]>(
            key.AsBytes(),
            replyPayload.EncryptedReplyMessage));
        Trace.TraceInformation(message);
        FlowLogger.NewEvent(me.PublicKey, "OnResponseReady");
        FlowLogger.NewConnected(message, me.PublicKey, "connected");
    }

    public void OnPaymentStatusChange(GigGossipNode me, string status, PaymentData paydata)
    {
    }
}

public class ApplicationSettings
{
    public required string FlowLoggerPath { get; set; }
}

public class SettlerAdminSettings
{
    public required Uri SettlerOpenApi { get; set; }
    public required string PrivateKey { get; set; }
}

public class NodeSettings
{
    public required string ConnectionString { get; set; }
    public required Uri GigWalletOpenApi { get; set; }
    public required string NostrRelays { get; set; }
    public required string PrivateKey { get; set; }
    public required Uri SettlerOpenApi { get; set; }
    public required long PriceAmountForRouting { get; set; }
    public required long BroadcastConditionsTimeoutMs { get; set; }
    public required string BroadcastConditionsPowScheme { get; set; }
    public required int BroadcastConditionsPowComplexity { get; set; }
    public required long TimestampToleranceMs { get; set; }
    public required long InvoicePaymentTimeoutSec { get; set; }
    public required int ChunkSize { get; set; }
    public required int Fanout { get; set; }

    public string[] GetNostrRelays()
    {
        return (from s in JsonArray.Parse(NostrRelays)!.AsArray() select s.GetValue<string>()).ToArray();
    }

    public GigLNDWalletAPIClient.swaggerClient GetLndWalletClient(HttpClient httpClient)
    {
        return new GigLNDWalletAPIClient.swaggerClient(GigWalletOpenApi.AbsoluteUri, httpClient);
    }
}


public class BitcoinSettings
{
    public required string AuthenticationString { get; set; }
    public required string HostOrUri { get; set; }
    public required string Network { get; set; }
    public required string WalletName { get; set; }

    public NBitcoin.Network GetNetwork()
    {
        if (Network.ToLower() == "main")
            return NBitcoin.Network.Main;
        if (Network.ToLower() == "testnet")
            return NBitcoin.Network.TestNet;
        if (Network.ToLower() == "regtest")
            return NBitcoin.Network.RegTest;
        throw new NotImplementedException();
    }

    public RPCClient NewRPCClient()
    {
        return new RPCClient(AuthenticationString, HostOrUri, GetNetwork());
    }

}