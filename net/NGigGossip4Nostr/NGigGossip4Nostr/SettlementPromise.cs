﻿using System;
namespace NGigGossip4Nostr;
public class SettlementPromise : SignableObject
{
    public Certificate SettlerCertificate { get; set; }
    public byte[] NetworkPaymentHash { get; set; }
    public byte[] HashOfEncryptedReplyPayload { get; set; }
    public int ReplyPaymentAmount { get; set; }

    public bool VerifyAll(byte[] encryptedSignedReplyPayload)
    {
        if (!this.SettlerCertificate.Verify())
        {
            return false;
        }

        if (!this.Verify(this.SettlerCertificate.PublicKey))
        {
            return false;
        }

        if (!Crypto.ComputeSha256(new List<byte[]>() { encryptedSignedReplyPayload }).SequenceEqual(this.HashOfEncryptedReplyPayload))
        {
            return false;
        }

        return true;
    }
}