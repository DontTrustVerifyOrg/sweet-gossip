﻿using System;
using CryptoToolkit;
using NBitcoin.Secp256k1;

namespace NGigGossip4Nostr;

[Serializable]
public class SettlementPromise : SignableObject
{
    public Uri ServiceUri { get; set; }
    public byte[] NetworkPaymentHash { get; set; }
    public byte[] HashOfEncryptedReplyPayload { get; set; }
    public long ReplyPaymentAmount { get; set; }

    public new bool Verify(byte[] encryptedSignedReplyPayload, ICertificationAuthorityAccessor caAccessor)
    {
        if (!base.Verify(caAccessor.GetPubKey(ServiceUri)))
            return false;

        if (!Crypto.ComputeSha256(encryptedSignedReplyPayload).SequenceEqual(this.HashOfEncryptedReplyPayload))
            return false;

        return true;
    }

    public new void Sign(ECPrivKey settlerPrivateKey)
    {
        base.Sign(settlerPrivateKey);
    }

    public SettlementPromise DeepCopy()
    {
        return new SettlementPromise()
        {
            ServiceUri = this.ServiceUri,
            NetworkPaymentHash = this.NetworkPaymentHash.ToArray(),
            HashOfEncryptedReplyPayload = this.HashOfEncryptedReplyPayload.ToArray(),
            ReplyPaymentAmount = this.ReplyPaymentAmount,
            Signature = this.Signature.ToArray()
        };
    }
}