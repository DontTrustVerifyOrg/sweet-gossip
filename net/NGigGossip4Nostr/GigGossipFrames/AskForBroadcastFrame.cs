﻿using System;
using CryptoToolkit;

namespace NGigGossip4Nostr;

/// <summary>
/// Represents a request for a broadcast.
/// </summary>
[Serializable]
public class AskForBroadcastFrame
{
    /// <summary>
    /// Gets or sets the signed payload for the request. This contains the necessary data for processing the request.
    /// </summary>
    public required Certificate<RequestPayloadValue> SignedRequestPayload { get; set; }
}

