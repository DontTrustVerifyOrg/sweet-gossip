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

    public BasicTest(string[] args)
    {
        this.args = args;
        var config = GetConfigurationRoot(".giggossip", "basictest.conf");
        gigWorkerSettings = config.GetSection("gigworker").Get<NodeSettings>();
        customerSettings = config.GetSection("customer").Get<NodeSettings>();
        settlerAdminSettings = config.GetSection("settleradmin").Get<SettlerAdminSettings>();
        bitcoinSettings = config.GetSection("bitcoin").Get<BitcoinSettings>();
    }


    HttpClient httpClient = new HttpClient();
    SimpleSettlerSelector settlerSelector = new SimpleSettlerSelector();

    public bool IsRunning { get; set; } = true;

    public void Run()
    {

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
        var settlerClient = settlerSelector.GetSettlerClient(settlerAdminSettings.SettlerOpenApi);
        var gtok = settlerClient.GetTokenAsync(settlerPubKey.AsHex()).Result;
        var token = Crypto.MakeSignedTimedToken(settlerPrivKey, DateTime.Now, gtok);
        var val = Convert.ToBase64String(Encoding.Default.GetBytes("ok"));

        var gigWorker = new GigGossipNode(
            gigWorkerSettings.ConnectionString,
            gigWorkerSettings.PrivateKey.AsECPrivKey(),
            gigWorkerSettings.GetNostrRelays(),
            gigWorkerSettings.ChunkSize
            );

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
            gigWorkerSettings.GetLndWalletClient(httpClient),
            settlerSelector);
        //await gigWorker.LoadCertificates(gigWorkerSettings.SettlerOpenApi);

        customer.Init(
            customerSettings.Fanout,
            customerSettings.PriceAmountForRouting,
            TimeSpan.FromMilliseconds(customerSettings.BroadcastConditionsTimeoutMs),
            customerSettings.BroadcastConditionsPowScheme,
            customerSettings.BroadcastConditionsPowComplexity,
            TimeSpan.FromMilliseconds(customerSettings.TimestampToleranceMs),
            TimeSpan.FromSeconds(customerSettings.InvoicePaymentTimeoutSec),
            customerSettings.GetLndWalletClient(httpClient),
            settlerSelector);

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
        customer.Start(new CustomerGossipNodeEvents(this));

        gigWorker.AddContact(new NostrContact() { PublicKey = customer.PublicKey, Petname = "Customer", Relay = "" });
        customer.AddContact(new NostrContact() { PublicKey = gigWorker.PublicKey, Petname = "GigWorker", Relay = "" });

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

        while (this.IsRunning)
        {
            lock(this)
            {
                Monitor.Wait(this);
            }
        }

        gigWorker.Stop();
        customer.Stop();
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
            me.AcceptBraodcast( peerPublicKey, broadcastFrame,
                new AcceptBroadcastResponse()
                {
                    Message = Encoding.Default.GetBytes($"mynameis={me.PublicKey}"),
                    Fee = 4321,
                    SettlerServiceUri = settlerUri,
                    MyCertificate = selectedCertificate
                });
        }
    }

    public void OnNewResponse(GigGossipNode me, ReplyPayload replyPayload, string replyInvoice, PayReq decodedReplyInvoice, string networkInvoice, PayReq decodedNetworkInvoice)
    {
    }

    public void OnResponseReady(GigGossipNode me, ReplyPayload replyPayload, string key)
    {
    }
}

public class CustomerGossipNodeEvents : IGigGossipNodeEvents
{
    BasicTest basicTest;
    public CustomerGossipNodeEvents(BasicTest basicTest)
    {
        this.basicTest = basicTest;
    }

    public void OnAcceptBroadcast(GigGossipNode me, string peerPublicKey, POWBroadcastFrame broadcastFrame)
    {
    }

    public void OnNewResponse(GigGossipNode me, ReplyPayload replyPayload, string replyInvoice, PayReq decodedReplyInvoice, string networkInvoice, PayReq decodedNetworkInvoice)
    {
        me.AcceptResponse(replyPayload, replyInvoice,decodedReplyInvoice,networkInvoice,decodedNetworkInvoice);
    }

    public void OnResponseReady(GigGossipNode me, ReplyPayload replyPayload, string key)
    {
        var message = Crypto.SymmetricDecrypt<byte[]>(
            key.AsBytes(),
            replyPayload.EncryptedReplyMessage);
        Trace.TraceInformation(Encoding.Default.GetString(message));
        lock(basicTest)
        {
            basicTest.IsRunning = false;
            Monitor.PulseAll(basicTest);
        }
    }
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
    public long PriceAmountForRouting { get; set; }
    public long BroadcastConditionsTimeoutMs { get; set; }
    public required string BroadcastConditionsPowScheme { get; set; }
    public int BroadcastConditionsPowComplexity { get; set; }
    public long TimestampToleranceMs { get; set; }
    public long InvoicePaymentTimeoutSec { get; set; }
    public int ChunkSize { get; set; }
    public int Fanout { get; set; }

    public string[] GetNostrRelays()
    {
        return (from s in JsonArray.Parse(NostrRelays).AsArray() select s.GetValue<string>()).ToArray();
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