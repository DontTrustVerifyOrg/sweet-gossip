﻿using System;
namespace LNDWallet;


/// <summary>
/// Defines the error codes for the LND Wallet.
/// </summary>
public enum LNDWalletErrorCode
{
    /// <summary>Indicates no error.</summary>
    Ok = 0,

    /// <summary>Indicates an invalid authentication token.</summary>
    InvalidToken = 1,

    /// <summary>Indicates insufficient funds.</summary>
    NotEnoughFunds = 2,

    /// <summary>Indicates an unknown payment.</summary>
    UnknownPayment = 3,

    /// <summary>Indicates an unknown invoice.</summary>
    UnknownInvoice = 4,

    /// <summary>Indicates that the payout has already been completed.</summary>
    PayoutAlreadyCompleted = 5,
}

/// <summary>
/// Provides extension methods for the <see cref="LNDWalletErrorCode"/> enumeration.
/// </summary>
public static class Extensions
{
    static string[] errorMesssages = new string[]{
        "",
        "Invalid authToken",
        "Not enough funds",
        "Unknown payment",
        "Unknown invoice",
        "Payout is already completed",
    };

    /// <summary>
    /// Gets a user-friendly message that corresponds to the specified <paramref name="errorCode"/>.
    /// </summary>
    /// <param name="errorCode">The error code to get a message for.</param>
    /// <returns>A user-friendly message that describes the error.</returns>
    public static string Message(this LNDWalletErrorCode errorCode)
    {
        return errorMesssages[(int)errorCode];
    }
}

/// <summary>
/// Represents errors that occur while dealing with the LND Wallet.
/// </summary>
[Serializable]
public class LNDWalletException : Exception
{
    /// <summary>Represents the error code of the exception.</summary>
    LNDWalletErrorCode ErrorCode { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LNDWalletException"/> class with a specified error code.
    /// </summary>
    /// <param name="lndWalletErrorCode">The error code for exception.</param>
    public LNDWalletException(LNDWalletErrorCode lndWalletErrorCode) : base(lndWalletErrorCode.Message())
    {
        ErrorCode = lndWalletErrorCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LNDWalletException"/> class with a specified error code and a detailed description.
    /// </summary>
    /// <param name="lndWalletErrorCode">The error code for exception.</param>
    /// <param name="message">The detail message that describes the current exception.</param>
    public LNDWalletException(LNDWalletErrorCode lndWalletErrorCode, string message) : base(message)
    {
        ErrorCode = lndWalletErrorCode;
    }
}

