﻿
using GigGossip;
using GigLNDWalletAPIClient;
using NBitcoin;
using NetworkClientToolkit;
using NGigGossip4Nostr;

namespace RideShareCLIApp;

public class GigGossipNodeEvents : IGigGossipNodeEvents
{
    private readonly GigGossipNodeEventSource _gigGossipNodeEventSource;

    public GigGossipNodeEvents(GigGossipNodeEventSource gigGossipNodeEventSource)
    {
        _gigGossipNodeEventSource = gigGossipNodeEventSource;
    }

    public void OnAcceptBroadcast(GigGossipNode me, string peerPublicKey, BroadcastFrame broadcastFrame)
    {
        _gigGossipNodeEventSource.FireOnAcceptBroadcast(new AcceptBroadcastEventArgs()
        {
            GigGossipNode = me,
            PeerPublicKey = peerPublicKey,
            BroadcastFrame = broadcastFrame
        });
    }

    public async void OnNetworkInvoiceAccepted(GigGossipNode me, InvoiceData iac)
    {
        _gigGossipNodeEventSource.FireOnNetworkInvoiceAccepted(new NetworkInvoiceAcceptedEventArgs
        {
            GigGossipNode = me,
            InvoiceData = iac
        });
    }

    public void OnInvoiceSettled(GigGossipNode me, Uri serviceUri, string paymentHash, string preimage)
    {
        _gigGossipNodeEventSource.FireOnInvoiceSettled(new InvoiceSettledEventArgs()
        {
            GigGossipNode = me,
            PaymentHash = paymentHash,
            Preimage = preimage,
            ServiceUri = serviceUri
        });
    }

    public void OnNewResponse(GigGossipNode me, JobReply replyPayloadCert, string replyInvoice, PaymentRequestRecord decodedReplyInvoice, string networkInvoice, PaymentRequestRecord decodedNetworkInvoice)
    {
        _gigGossipNodeEventSource.FireOnNewResponse(new NewResponseEventArgs()
        {
            GigGossipNode = me,
            ReplyPayloadCert = replyPayloadCert,
            ReplyInvoice = replyInvoice,
            DecodedReplyInvoice = decodedReplyInvoice,
            NetworkPaymentRequest = networkInvoice,
            DecodedNetworkInvoice = decodedNetworkInvoice,
        });
    }

    public void OnResponseReady(GigGossipNode me, JobReply replyPayload, string key)
    {
        var reply = replyPayload.Header.EncryptedReply.Decrypt<Reply>(key.AsBytes());

        _gigGossipNodeEventSource.FireOnResponseReady(new ResponseReadyEventArgs()
        {
            GigGossipNode = me,
            ReplierCertificateId = replyPayload.Header.JobReplyId.AsGuid(),
            RequestPayloadId = replyPayload.Header.JobRequest.Header.JobRequestId.AsGuid(),
            Reply = reply
        });
    }


    public void OnResponseCancelled(GigGossipNode me, JobReply replyPayload)
    {
        _gigGossipNodeEventSource.FireOnResponseCancelled(new ResponseCancelledEventArgs()
        {
            GigGossipNode = me,
            ReplierCertificateId = replyPayload.Header.JobReplyId.AsGuid(),
            RequestPayloadId = replyPayload.Header.JobRequest.Header.JobRequestId.AsGuid(),
        });
    }

    public void OnPaymentStatusChange(GigGossipNode me, PaymentStatus status, PaymentData paydata)
    {
        _gigGossipNodeEventSource.FireOnPaymentStatusChange(new PaymentStatusChangeEventArgs()
        {
            GigGossipNode = me,
            PaymentData = paydata,
            Status = status
        });
    }

    public void OnCancelBroadcast(GigGossipNode me, string peerPublicKey, CancelBroadcastFrame broadcastFrame)
    {
        _gigGossipNodeEventSource.FireOnCancelBroadcast(new CancelBroadcastEventArgs
        {
            GigGossipNode = me,
            CancelBroadcastFrame = broadcastFrame,
            PeerPublicKey = peerPublicKey
        });
    }

    public void OnNetworkInvoiceCancelled(GigGossipNode me, InvoiceData iac)
    {
        _gigGossipNodeEventSource.FireOnNetworkInvoiceCancelled(new NetworkInvoiceCancelledEventArgs
        {
            GigGossipNode = me,
            InvoiceData = iac
        });
    }

    public void OnInvoiceAccepted(GigGossipNode me, InvoiceData iac)
    {
        _gigGossipNodeEventSource.FireOnInvoiceAccepted(new InvoiceAcceptedEventArgs
        {
            GigGossipNode = me,
            InvoiceData = iac
        });
    }

    public void OnInvoiceCancelled(GigGossipNode me, InvoiceData iac)
    {
        _gigGossipNodeEventSource.FireOnInvoiceCancelled(new InvoiceCancelledEventArgs
        {
            GigGossipNode = me,
            InvoiceData = iac
        });
    }

    public void OnNewContact(GigGossipNode me, string pubkey)
    {
        _gigGossipNodeEventSource.FireOnNewContact(new NewContactEventArgs
        {
            GigGossipNode = me,
            PublicKey = pubkey
        });
    }

    public void OnSettings(GigGossipNode me, string settings)
    {
    }

    public void OnEoseArrived(GigGossipNode me)
    {
    }

    public void OnServerConnectionState(GigGossipNode me, ServerConnectionSource source, ServerConnectionState state, Uri uri)
    {
        _gigGossipNodeEventSource.FireOnServerConnectionState(new ServerConnectionSourceStateEventArgs
        {
            GigGossipNode = me,
            Source = source,
            State = state,
            Uri = uri
        });
    }
}

