﻿using System;
using NGigGossip4Nostr;
using System.Diagnostics;
using NBitcoin.Protocol;
using NBitcoin.Secp256k1;
using System.Numerics;
using System.Reflection;
using System.Buffers.Text;
using System.Threading.Channels;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using NNostr.Client;
using CryptoToolkit;

public class ResponseEventArgs : EventArgs
{
    public ReplyPayload payload { get; set; }
    public string network_invoice { get; set; }
}

public class GigGossipNode : NostrNode
{

    protected Certificate certificate;
    protected int priceAmountForRouting;
    protected TimeSpan broadcastConditionsTimeout;
    protected string broadcastConditionsPowScheme;
    protected int broadcastConditionsPowComplexity;
    protected TimeSpan timestampTolerance;
    protected TimeSpan invoicePaymentTimeout;
    protected HashSet<string> _knownHosts;
    protected Dictionary<Guid, BroadcastPayload> _broadcastPayloadsByAskId;
    protected Dictionary<Guid, POWBroadcastConditionsFrame> _myPowBrCondByAskId;
    protected Dictionary<Guid, int> _alreadyBroadcastedRequestPayloadIds;
    protected Dictionary<Guid, Dictionary<ECXOnlyPubKey, List<Tuple<ReplyPayload, string>>>> replyPayloads;
    protected Dictionary<string, string> nextNetworkInvoiceToPay;
    protected Dictionary<string, ReplyPayload> replyPayloadsByHodlInvoicePaymentHash;
    protected GigLNDWalletAPIClient.swaggerClient lndWalletClient;
    protected GigGossipSettlerAPIClient.swaggerClient settlerClient;
    protected Guid _walletToken;
    protected Guid _settlerToken;

    protected ICertificationAuthorityAccessor certificationAuthorityAccessor;


    public GigGossipNode( ECPrivKey privKey, string[] nostrRelays) : base(privKey, nostrRelays)
    {
    }

    protected async void Init(Certificate certificate, 
                           int priceAmountForRouting, TimeSpan broadcastConditionsTimeout, string broadcastConditionsPowScheme,
                           int broadcastConditionsPowComplexity, TimeSpan timestampTolerance, TimeSpan invoicePaymentTimeout,
                           GigLNDWalletAPIClient.swaggerClient lndWalletClient, GigGossipSettlerAPIClient.swaggerClient settlerClient)
    {
        this.certificate = certificate;
        this.priceAmountForRouting = priceAmountForRouting;
        this.broadcastConditionsTimeout = broadcastConditionsTimeout;
        this.broadcastConditionsPowScheme = broadcastConditionsPowScheme;
        this.broadcastConditionsPowComplexity = broadcastConditionsPowComplexity;
        this.timestampTolerance = timestampTolerance;
        this.invoicePaymentTimeout = invoicePaymentTimeout;

        this._knownHosts = new();
        this._broadcastPayloadsByAskId = new();
        this._myPowBrCondByAskId = new();
        this._alreadyBroadcastedRequestPayloadIds = new();
        this.replyPayloads = new();
        this.nextNetworkInvoiceToPay = new();
        this.replyPayloadsByHodlInvoicePaymentHash = new();
        this.lndWalletClient = lndWalletClient;
        this._walletToken = await lndWalletClient.GetTokenAsync(this.Name);
        this.settlerClient = settlerClient;
        this._settlerToken = await settlerClient.GetTokenAsync(this.Name);
    }

    protected string walletToken()
    {
        return Crypto.MakeSignedTimedToken(this._privateKey, DateTime.Now, this._walletToken);
    }
    protected string settlerToken()
    {
        return Crypto.MakeSignedTimedToken(this._privateKey, DateTime.Now, this._settlerToken);
    }

    public void ConnectTo(string otherName)
    {
        if (otherName == this.Name)
            throw new Exception("Cannot connect node to itself");
        this._knownHosts.Add(otherName);
    }

    public virtual bool AcceptTopic(AbstractTopic topic)
    {
        return false;
    }

    public void IncrementBroadcasted(Guid payloadId)
    {
        if (!_alreadyBroadcastedRequestPayloadIds.ContainsKey(payloadId))
            _alreadyBroadcastedRequestPayloadIds[payloadId] = 0;
        _alreadyBroadcastedRequestPayloadIds[payloadId] += 1;
    }

    public bool CanIncrementBroadcast(Guid payloadId)
    {
        if (!_alreadyBroadcastedRequestPayloadIds.ContainsKey(payloadId))
            return true;
        return _alreadyBroadcastedRequestPayloadIds[payloadId] <= 2;
    }

    public void Broadcast(RequestPayload requestPayload,
                          string? originatorPeerName = null,
                          OnionRoute? backwardOnion = null)
    {
        if (!this.AcceptTopic(requestPayload.Topic))
        {
            return;
        }

        this.IncrementBroadcasted(requestPayload.PayloadId);

        if (!this.CanIncrementBroadcast(requestPayload.PayloadId))
        {
            Trace.TraceInformation("already broadcasted");
            return;
        }

        foreach (var peerName in _knownHosts)
        {
            if (peerName == originatorPeerName)
                continue;

            AskForBroadcastFrame askForBroadcastFrame = new AskForBroadcastFrame()
            {
                SignedRequestPayload = requestPayload,
                AskId = Guid.NewGuid()
            };

            BroadcastPayload broadcastPayload = new BroadcastPayload()
            {
                SignedRequestPayload = requestPayload,
                BackwardOnion = (backwardOnion ?? new OnionRoute()).Grow(
                    new OnionLayer(this.Name),
                    Context.Instance.CreateXOnlyPubKey(Convert.FromHexString(peerName))),
                Timestamp = null
            };

            this._broadcastPayloadsByAskId[askForBroadcastFrame.AskId] = broadcastPayload;
            this.SendMessage(peerName, askForBroadcastFrame);
        }
    }

    public void OnAskForBroadcastFrame(string peerName, AskForBroadcastFrame askForBroadcastFrame)
    {
        if (!CanIncrementBroadcast(askForBroadcastFrame.SignedRequestPayload.PayloadId))
        {
            Trace.TraceInformation("already broadcasted, don't ask");
            return;
        }
        POWBroadcastConditionsFrame powBroadcastConditionsFrame = new POWBroadcastConditionsFrame()
        {
            AskId = askForBroadcastFrame.AskId,
            ValidTill = DateTime.Now.Add(this.broadcastConditionsTimeout),
            WorkRequest = new WorkRequest()
            {
                PowScheme = this.broadcastConditionsPowScheme,
                PowTarget = ProofOfWork.PowTargetFromComplexity(this.broadcastConditionsPowScheme, this.broadcastConditionsPowComplexity)
            },
            TimestampTolerance = this.timestampTolerance
        };

        _myPowBrCondByAskId[powBroadcastConditionsFrame.AskId] = powBroadcastConditionsFrame;
        SendMessage(peerName, powBroadcastConditionsFrame);
    }

    public void OnPOWBroadcastConditionsFrame(string peerName, POWBroadcastConditionsFrame powBroadcastConditionsFrame)
    {
        if (DateTime.Now <= powBroadcastConditionsFrame.ValidTill)
        {
            if (_broadcastPayloadsByAskId.ContainsKey(powBroadcastConditionsFrame.AskId))
            {
                BroadcastPayload broadcastPayload = _broadcastPayloadsByAskId[powBroadcastConditionsFrame.AskId];
                broadcastPayload.SetTimestamp(DateTime.Now);
                var pow = powBroadcastConditionsFrame.WorkRequest.ComputeProof(broadcastPayload);    // This will depend on your computeProof method implementation
                POWBroadcastFrame powBroadcastFrame = new POWBroadcastFrame()
                {
                    AskId = powBroadcastConditionsFrame.AskId,
                    BroadcastPayload = broadcastPayload,
                    ProofOfWork = pow
                };
                SendMessage(peerName, powBroadcastFrame);
            }
        }
    }

    public virtual Tuple<byte[]?, int> AcceptBroadcast(RequestPayload signedRequestPayload)
    {
        return new Tuple<byte[]?, int>(null, 0);
    }

    public async void OnPOWBroadcastFrame(string peerName, POWBroadcastFrame powBroadcastFrame)
    {
        if (!_myPowBrCondByAskId.ContainsKey(powBroadcastFrame.AskId))
            return;

        var myPowBroadcastConditionFrame = _myPowBrCondByAskId[powBroadcastFrame.AskId];

        if (powBroadcastFrame.ProofOfWork.PowScheme != myPowBroadcastConditionFrame.WorkRequest.PowScheme)
            return;

        if (powBroadcastFrame.ProofOfWork.PowTarget != myPowBroadcastConditionFrame.WorkRequest.PowTarget)
            return;

        if (powBroadcastFrame.BroadcastPayload.Timestamp > DateTime.Now)
            return;

        if (powBroadcastFrame.BroadcastPayload.Timestamp + myPowBroadcastConditionFrame.TimestampTolerance < DateTime.Now)
            return;

        if (!powBroadcastFrame.Verify(certificationAuthorityAccessor))
            return;

        var messageAndFeeTuple = this.AcceptBroadcast(powBroadcastFrame.BroadcastPayload.SignedRequestPayload);
        var message = messageAndFeeTuple.Item1;
        var fee = messageAndFeeTuple.Item2;

        if (message != null)
        {
            var rpt = await this.settlerClient.GenerateReplyPaymentTrustAsync(this.Name, settlerToken(), powBroadcastFrame.AskId.ToString());
            var replyPaymentHash = rpt.PaymentHash;

            var replyInvoice = (await lndWalletClient.AddHodlInvoiceAsync(this.Name, walletToken() , fee, replyPaymentHash.AsHex(), "")).PaymentRequest;

            var signedRequestPayloadSerialized = Crypto.SerializeObject(powBroadcastFrame.BroadcastPayload.SignedRequestPayload);
            var replierCertificateSerialized = Crypto.SerializeObject(this.certificate);
            var settr = await this.settlerClient.GenerateSettlementTrustAsync(this.Name, settlerToken(), message, replyInvoice, signedRequestPayloadSerialized, replierCertificateSerialized);
            var settlementTrust = (SettlementTrust)Crypto.DeserializeObject(settr);
            var signedSettlementPromise = settlementTrust.SettlementPromise;
            var networkInvoice = settlementTrust.NetworkInvoice;
            var encryptedReplyPayload = settlementTrust.EncryptedReplyPayload;

            var responseFrame = new ReplyFrame()
            {
                EncryptedReplyPayload = encryptedReplyPayload,
                SignedSettlementPromise = signedSettlementPromise,
                ForwardOnion = powBroadcastFrame.BroadcastPayload.BackwardOnion,
                NetworkInvoice = networkInvoice
            };

            await this.OnResponseFrame(peerName, responseFrame, newResponse: true);
        }
        else
        {
            this.Broadcast(
                requestPayload: powBroadcastFrame.BroadcastPayload.SignedRequestPayload,
                originatorPeerName: peerName,
                backwardOnion: powBroadcastFrame.BroadcastPayload.BackwardOnion);
        }
    }

    public event EventHandler<ResponseEventArgs> OnNewResponse;

    public async Task OnResponseFrame(string peerName, ReplyFrame responseFrame, bool newResponse = false)
    {
        var decodedInvoice = await lndWalletClient.DecodeInvoiceAsync(this.Name, walletToken(), responseFrame.NetworkInvoice);
        if (responseFrame.ForwardOnion.IsEmpty())
        {
            if (!responseFrame.SignedSettlementPromise.NetworkPaymentHash.AsHex().SequenceEqual(decodedInvoice.PaymentHash))
            {
                Trace.TraceError("reply payload has different network_payment_hash than network_invoice");
                return;
            }

            ReplyPayload replyPayload = responseFrame.DecryptAndVerify(_privateKey, responseFrame.SignedSettlementPromise.SettlerCertificate.PublicKey, this.certificationAuthorityAccessor);
            if (replyPayload == null)
            {
                Trace.TraceError("reply payload mismatch");
                return;
            }
            var payloadId = replyPayload.SignedRequestPayload.PayloadId;
            if (!replyPayloads.ContainsKey(payloadId))
            {
                replyPayloads[payloadId] = new();
            }
            var replierId = replyPayload.ReplierCertificate.PublicKey;
            if (!replyPayloads[payloadId].ContainsKey(replierId))
            {
                replyPayloads[payloadId][replierId] = new();
            }

            replyPayloads[payloadId][replierId].Add(new Tuple<ReplyPayload, string>(replyPayload, responseFrame.NetworkInvoice));
            replyPayloadsByHodlInvoicePaymentHash[decodedInvoice.PaymentHash] = replyPayload;
            OnNewResponse.Invoke(this, new ResponseEventArgs() { network_invoice = responseFrame.NetworkInvoice, payload = replyPayload });
        }
        else
        {
            var topLayer = responseFrame.ForwardOnion.Peel(_privateKey);
            if (_knownHosts.Contains(topLayer.PeerName))
            {
                if (!responseFrame.SignedSettlementPromise.VerifyAll(responseFrame.EncryptedReplyPayload, this.certificationAuthorityAccessor))
                {
                    return;
                }
                if (!responseFrame.SignedSettlementPromise.NetworkPaymentHash.AsHex().SequenceEqual(decodedInvoice.PaymentHash))
                {
                    return;
                }
                if (!newResponse)
                {
                    var nextNetworkInvoice = responseFrame.NetworkInvoice;
                    var networkInvoice = await lndWalletClient.AddHodlInvoiceAsync( 
                        this.Name, this.walletToken(),
                        decodedInvoice.NumSatoshis + this.priceAmountForRouting,
                        decodedInvoice.PaymentHash, "");
                    //settler.RegisterForSettlementInPaymentChain(responseFrame.NetworkInvoice.Id,networkInvoice.Id);
                    this.nextNetworkInvoiceToPay[networkInvoice.PaymentHash] = nextNetworkInvoice;
                    responseFrame = responseFrame.DeepCopy();
                    responseFrame.NetworkInvoice = networkInvoice.PaymentRequest;
                }
                SendMessage(topLayer.PeerName, responseFrame);
            }
        }
    }



    public List<List<Tuple<ReplyPayload, string>>> GetResponses(Guid payloadId)
    {
        if (!replyPayloads.ContainsKey(payloadId))
        {
            Trace.TraceError("topic has no responses");
            return new();
        }
        return replyPayloads[payloadId].Values.ToList();
    }

    public async void AcceptResponse(ReplyPayload replyPayload, string networkInvoice)
    {
        var payloadId = replyPayload.SignedRequestPayload.PayloadId;
        if (!replyPayloads.ContainsKey(payloadId))
        {
            Trace.TraceError("topic has no responses");
            return;
        }

        if (!replyPayloads[payloadId].ContainsKey(replyPayload.ReplierCertificate.PublicKey))
        {
            Trace.TraceError("replier has not responded for this topic");
            return;
        }

        Trace.TraceInformation("accepting the network payment");

        await lndWalletClient.SendPaymentAsync(this.Name, walletToken(), networkInvoice, 10000);
    }

    public override void OnMessage(string senderNodeName, object frame)
    {
        if (frame is AskForBroadcastFrame)
        {
            OnAskForBroadcastFrame(senderNodeName, (AskForBroadcastFrame)frame);
        }
        else if (frame is POWBroadcastConditionsFrame)
        {
            OnPOWBroadcastConditionsFrame(senderNodeName, (POWBroadcastConditionsFrame)frame);
        }
        else if (frame is POWBroadcastFrame)
        {
            OnPOWBroadcastFrame(senderNodeName, (POWBroadcastFrame)frame);
        }
        else if (frame is ReplyFrame)
        {
            OnResponseFrame(senderNodeName, (ReplyFrame)frame);
        }
        else
        {
            Trace.TraceError("unknown request: ", senderNodeName, frame);
        }
    }

    public bool AcceptingHodlInvoice(string invoice)
    {
        return true;
    }

    public event EventHandler<ResponseEventArgs> OnResponseReady;

}
