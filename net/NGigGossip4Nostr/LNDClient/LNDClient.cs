﻿using Grpc.Core;
using Invoicesrpc;
using Lnrpc;
using NBitcoin;
using Routerrpc;

namespace LNDClient;

public static class LND
{
    public class NodesConfiguration
    {
        private List<string> macaroonPath = new();
        private List<string> tlsCertPath = new();
        private List<string> rpcHost = new();
        private List<string> nodeListenOn = new();
        public int AddNodeConfiguration(string macaroonPath, string tlsCertPath, string rpcHost, string nodeListenOn)
        {
            this.macaroonPath.Add(macaroonPath);
            this.tlsCertPath.Add(tlsCertPath);
            this.rpcHost.Add(rpcHost);
            this.nodeListenOn.Add(nodeListenOn);
            return this.macaroonPath.Count;
        }
        public string ListenHost(int idx)
        {
            return nodeListenOn[idx - 1];
        }

        public string RpcHost(int idx)
        {
            return rpcHost[idx - 1];
        }

        public string TlsCert(int idx)
        {
            return tlsCertPath[idx - 1];
        }
        public string MacaroonPath(int idx)
        {
            return macaroonPath[idx - 1];
        }
    }


    static Invoicesrpc.Invoices.InvoicesClient InvoicesClient(NodesConfiguration conf, int idx)
    {
        var channel = new Grpc.Core.Channel(conf.RpcHost(idx), GetSslCredentials(conf, idx));
        var client = new Invoicesrpc.Invoices.InvoicesClient(channel);
        return client;
    }

    static Routerrpc.Router.RouterClient RouterClient(NodesConfiguration conf, int idx)
    {
        var channel = new Grpc.Core.Channel(conf.RpcHost(idx), GetSslCredentials(conf, idx));
        var client = new Routerrpc.Router.RouterClient(channel);
        return client;
    }

    static Lnrpc.Lightning.LightningClient UserClient(NodesConfiguration conf, int idx)
    {
        var channel = new Grpc.Core.Channel(conf.RpcHost(idx), GetSslCredentials(conf, idx));
        var client = new Lnrpc.Lightning.LightningClient(channel);
        return client;
    }

    static SslCredentials GetSslCredentials(NodesConfiguration conf, int idx)
    {
        Environment.SetEnvironmentVariable("GRPC_SSL_CIPHER_SUITES", "HIGH+ECDSA");
        var cert = System.IO.File.ReadAllText(conf.TlsCert(idx));
        var sslCreds = new SslCredentials(cert);
        return sslCreds;
    }

    static string GetMacaroon(NodesConfiguration conf, int idx)
    {
        byte[] macaroonBytes = File.ReadAllBytes(conf.MacaroonPath(idx));
        var macaroon = BitConverter.ToString(macaroonBytes).Replace("-", "");
        // hex format stripped of "-" chars
        return macaroon;
    }

    static Metadata Metadata(NodesConfiguration conf, int idx)
    {
        return new Metadata() { new Metadata.Entry("macaroon", GetMacaroon(conf, idx)) };
    }

    public static string NewAddress(NodesConfiguration conf, int idx, string account)
    {
        var response = UserClient(conf, idx).NewAddress(
            new NewAddressRequest() { Type= AddressType.NestedPubkeyHash, Account = account },
            Metadata(conf, idx));
        return response.Address;
    }

    //-1 means send all
    public static string SendCoins(NodesConfiguration conf, int idx, string address, string memo, long satoshis = -1)
    {
        SendCoinsRequest req;
        if (satoshis > -1)
            req = new SendCoinsRequest() { Addr = address, Amount = satoshis, TargetConf = 6, Label = memo };
        else
            req = new SendCoinsRequest() { Addr = address, SendAll = true, TargetConf = 6, Label = memo };

        var response = UserClient(conf, idx).SendCoins(req, Metadata(conf, idx));
        return response.Txid;
    }

    public static WalletBalanceResponse GetWalletBalance(NodesConfiguration conf, int idx)
    {
        return UserClient(conf, idx).WalletBalance(
            new WalletBalanceRequest() ,
            Metadata(conf, idx));
    }

    public static string AddInvoice(NodesConfiguration conf, int idx, long satoshis, string memo)
    {
        var response = UserClient(conf, idx).AddInvoice(
            new Invoice()
            {
                Memo = memo,
                Value = satoshis,
            },
            Metadata(conf, idx));
        return response.PaymentRequest;
    }

    public static Invoice LookupInvoice(NodesConfiguration conf, int idx, byte[] hash)
    {
        return UserClient(conf, idx).LookupInvoice(
            new PaymentHash()
            {
                RHash = Google.Protobuf.ByteString.CopyFrom(hash)
            },
            Metadata(conf, idx));
    }

    public static PayReq DecodeInvoice(NodesConfiguration conf, int idx, string paymentRequest)
    {
        return UserClient(conf, idx).DecodePayReq(
            new PayReqString()
            {
                PayReq = paymentRequest
            },
            Metadata(conf, idx));
    }

    public static SendResponse SendPayment(NodesConfiguration conf, int idx, string paymentRequest)
    {
        return UserClient(conf, idx).SendPaymentSync(
            new SendRequest()
            {
                PaymentRequest = paymentRequest,
            },
            Metadata(conf, idx));
    }

    public static AsyncServerStreamingCall<Payment> SendPaymentV2(NodesConfiguration conf, int idx, string paymentRequest, int timeout)
    {
        var stream = RouterClient(conf, idx).SendPaymentV2(
            new SendPaymentRequest()
            {
                PaymentRequest = paymentRequest,
                TimeoutSeconds = timeout,
            },
            Metadata(conf, idx));
        return stream;
    }

    public static GetInfoResponse GetNodeInfo(NodesConfiguration conf, int idx)
    {
        return UserClient(conf, idx).GetInfo(
            new GetInfoRequest(),
            Metadata(conf, idx));
    }

    public static void Connect(NodesConfiguration conf, int idx, int idx2)
    {
        var nodeInfo = GetNodeInfo(conf, idx2);
        UserClient(conf, idx).ConnectPeer(
            new ConnectPeerRequest()
            {
                Addr = new LightningAddress() { Host = conf.ListenHost(idx2), Pubkey = nodeInfo.IdentityPubkey }
            },
            Metadata(conf, idx));
    }

    public static ListPeersResponse ListPeers(NodesConfiguration conf, int idx)
    {
        return UserClient(conf, idx).ListPeers(
            new ListPeersRequest(),
            Metadata(conf, idx));
    }

    public static AsyncServerStreamingCall<OpenStatusUpdate> OpenChannel(NodesConfiguration conf, int idx, string nodePubKey, long fundingSatoshis, string closeAddress = null)
    {
        OpenChannelRequest ocr = null;
        if (closeAddress == null)
            ocr = new OpenChannelRequest()
            {
                LocalFundingAmount = fundingSatoshis,
                NodePubkeyString = nodePubKey
            };
        else
            ocr = new OpenChannelRequest()
            {
                LocalFundingAmount = fundingSatoshis,
                NodePubkeyString = nodePubKey,
                CloseAddress = closeAddress,
            };
        return UserClient(conf, idx).OpenChannel(ocr, Metadata(conf, idx));
    }

    public static AsyncServerStreamingCall<CloseStatusUpdate> CloseChannel(NodesConfiguration conf, int idx, string fundingTxidStr)
    {
        var stream = UserClient(conf, idx).CloseChannel(
            new CloseChannelRequest()
            {
                ChannelPoint = new ChannelPoint() { FundingTxidStr = fundingTxidStr }
            },
            Metadata(conf, idx));
        return stream;
    }

    public static PendingChannelsResponse PendingChannels(NodesConfiguration conf, int idx)
    {
        return UserClient(conf, idx).PendingChannels(
            new PendingChannelsRequest() { },
            Metadata(conf, idx));
    }

    public static ListChannelsResponse ListChannels(NodesConfiguration conf, int idx, bool activeOnly = true)
    {
        return UserClient(conf, idx).ListChannels(
            new ListChannelsRequest()
            {
                ActiveOnly = activeOnly
            },
            Metadata(conf, idx));
    }

    public static AddHoldInvoiceResp AddHodlInvoice(NodesConfiguration conf, int idx, long satoshis, string memo, byte[] hash, long expiry = 86400)
    {
        return InvoicesClient(conf, idx).AddHoldInvoice(
            new AddHoldInvoiceRequest()
            {
                Memo = memo,
                Value = satoshis,
                Hash = Google.Protobuf.ByteString.CopyFrom(hash),
                Expiry = expiry
            },
            Metadata(conf, idx));
    }

    public static void CancelInvoice(NodesConfiguration conf, int idx, byte[] hash)
    {
        InvoicesClient(conf, idx).CancelInvoice(
            new CancelInvoiceMsg()
            {
                PaymentHash = Google.Protobuf.ByteString.CopyFrom(hash),
            },
            Metadata(conf, idx));
    }

    public static void SettleInvoice(NodesConfiguration conf, int idx, byte[] preimage)
    {
        InvoicesClient(conf, idx).SettleInvoice(
            new SettleInvoiceMsg()
            {
                Preimage = Google.Protobuf.ByteString.CopyFrom(preimage)
            },
            Metadata(conf, idx));
    }

    public static byte[] ComputePaymentHash(byte[] preimage)
    {
        Span<byte> buf = stackalloc byte[32];
        var sha256 = System.Security.Cryptography.SHA256.Create();
        sha256.TryComputeHash(preimage, buf, out _);
        return buf.ToArray();
    }

    public static byte[] GenerateRandomPreimage()
    {
        return RandomUtils.GetBytes(32);
    }

    public static Invoice LookupInvoiceV2(NodesConfiguration conf, int idx, byte[] hash)
    {
        return InvoicesClient(conf, idx).LookupInvoiceV2(new LookupInvoiceMsg(){
                PaymentHash = Google.Protobuf.ByteString.CopyFrom(hash)
            },
            Metadata(conf, idx));
    }

    public static AsyncServerStreamingCall<Invoice> SubscribeSingleInvoice(NodesConfiguration conf, int idx, byte[] hash)
    {
        var stream = InvoicesClient(conf, idx).SubscribeSingleInvoice(
            new SubscribeSingleInvoiceRequest()
            {
                RHash = Google.Protobuf.ByteString.CopyFrom(hash),
            }, Metadata(conf, idx));

        return stream;
    }

}

