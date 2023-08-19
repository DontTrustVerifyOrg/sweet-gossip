﻿using System;
using CryptoToolkit;
using NBitcoin.Secp256k1;

namespace NGigGossip4Nostr;

/// <summary>
/// Represents a settlement promise.
/// </summary>
[Serializable]
public class SettlementPromise : SignableObject
{
    /// <summary>
    /// Gets or sets the service URI of the Settler.
    /// </summary>
    public Uri ServiceUri { get; set; }

    /// <summary>
    /// Gets or sets the network payment hash.
    /// </summary>
    public byte[] NetworkPaymentHash { get; set; }

    /// <summary>
    /// Gets or sets the hash of encrypted reply payload.
    /// </summary>
    public byte[] HashOfEncryptedReplyPayload { get; set; }

    /// <summary>
    /// Gets or sets the reply payment amount.
    /// </summary>
    public long ReplyPaymentAmount { get; set; }

    /// <summary>
    /// Verifies the settlement promise.
    /// </summary>
    /// <param name="encryptedSignedReplyPayload">The encrypted signed reply payload.</param>
    /// <param name="caAccessor">The certification authority accessor.</param>
    /// <returns><c>true</c> if the verification was successful; otherwise, <c>false</c>.</returns>
    public new bool Verify(byte[] encryptedSignedReplyPayload, ICertificationAuthorityAccessor caAccessor)
    {
        if (!base.Verify(caAccessor.GetPubKey(ServiceUri)))
            return false;

        if (!Crypto.ComputeSha256(encryptedSignedReplyPayload).SequenceEqual(this.HashOfEncryptedReplyPayload))
            return false;

        return true;
    }

    /// <summary>
    /// Signs the settlement promise using a given private key.
    /// Overrides the base method from the <see cref="SignableObject"/> class.
    /// </summary>
    /// <param name="settlerPrivateKey">
    /// The private key to use for signing.
    /// </param>
    /// <see cref="SignableObject.Sign(ECPrivKey)"/>
    public new void Sign(ECPrivKey settlerPrivateKey)
    {
        base.Sign(settlerPrivateKey);
    }

    /// <summary>
    /// Create a deep copy of this settlement promise.
    /// </summary>
    /// <returns>A deep copy of this instance.</returns>
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