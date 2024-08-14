﻿
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using NBitcoin;
using NBitcoin.Secp256k1;
using System.Diagnostics.CodeAnalysis;
using NBitcoin.JsonConverters;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Snappier;
using ProtoBuf;
using System.Runtime.ConstrainedExecution;

namespace CryptoToolkit;

public interface IProtoFrame
{
}

/// <summary>
/// This static class contains extension methods for working with hexadecimal values.
/// </summary>
public static class HexExtensions
{
    public static string AsHex(this ECPrivKey key)
    {
        Span<byte> span = stackalloc byte[32];
        key.WriteToSpan(span);
        return span.AsHex();
    }
    public static string AsHex(this Span<byte> bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public static string AsHex(this byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
    public static string AsHex(this ECXOnlyPubKey key)
    {
        return key.ToBytes().AsSpan().AsHex();
    }
    public static ECPrivKey AsECPrivKey(this string key)
    {
        return Context.Instance.CreateECPrivKey(Convert.FromHexString(key));
    }
    public static async Task<ECPrivKey> AsECPrivKeyAsync(this string key)
    {
        ECPrivKey? result = null;
        await Task.Run(() =>
        {
            result = Context.Instance.CreateECPrivKey(Convert.FromHexString(key));
        });
        return result!;
    }
    public static ECXOnlyPubKey AsECXOnlyPubKey(this string key)
    {
        return Context.Instance.CreateXOnlyPubKey(Convert.FromHexString(key));
    }
    public static byte[] AsBytes(this string data)
    {
        return Convert.FromHexString(data);
    }
}

/// <summary>
/// The Crypto class provides utilities for cryptographic operations such as signing objects, verifying signatures, encryption and decryption.
/// </summary>
public static class Crypto
{
    /// <summary>
    /// A struct to represent a timed GUID token with its signature. Used in API calls.
    /// </summary>
    [ProtoContract]
    public struct TimedGuidToken : IProtoFrame
    {
        [ProtoMember(1)]
        public string PublicKey { get; set; }
        [ProtoMember(2)]
        public DateTime DateTime { get; set; }
        [ProtoMember(3)]
        public Guid Guid { get; set; }
        [ProtoMember(4)]
        public byte[]? Signature { get; set; }
    }

    /// <summary>
    /// Creates a signed timed token using a provided private key, date time and guid.
    /// </summary>
    public static string MakeSignedTimedToken(ECPrivKey ecpriv, DateTime dateTime, Guid guid)
    {
        var tt = new TimedGuidToken();
        tt.PublicKey = ecpriv.CreateXOnlyPubKey().AsHex();
        tt.DateTime = dateTime;
        tt.Guid = guid;
        tt.Signature = SignObject(tt, ecpriv);
        return Convert.ToBase64String(BinarySerializeObject(tt));
    }

    /// <summary>
    /// Verifies the validity of a signed timed token. Returns the timed token if it is valid within a given period of seconds. Returns null otherwise.
    /// </summary>
    public static TimedGuidToken? VerifySignedTimedToken(string TimedTokenBase64, double seconds)
    {
        var serialized = Convert.FromBase64String(TimedTokenBase64);
        TimedGuidToken timedToken = BinaryDeserializeObject<TimedGuidToken>(serialized);
        var ecpub = Context.Instance.CreateXOnlyPubKey(Convert.FromHexString(timedToken.PublicKey));
        if ((DateTime.UtcNow - timedToken.DateTime).TotalSeconds > seconds)
            return null;
        var signature = timedToken.Signature;
        timedToken.Signature = null;
        if (!VerifyObject(timedToken, signature!, ecpub))
            return null;
        return timedToken;
    }

    /// <summary>
    /// Computes a SHA256 hash of an array of bytes representing a preimage for a payment. Used in Lightning Network HODL invoices for manual settlement.
    /// </summary>
    public static byte[] ComputePaymentHash(byte[] preimage)
    {
        return ComputeSha256(preimage);
    }

    /// <summary>
    /// Generates a random preimage for Lightning Network HODL invoice.
    /// </summary>
    public static byte[] GenerateRandomPreimage()
    {
        return RandomUtils.GetBytes(32);
    }

    /// <summary>
    /// Structure that encapsulates data and initial vector (IV) used for symmetric encryption.
    /// </summary>
    public struct EncryptedData
    {
        public byte[] Data;
        public byte[] IV;
    }

    public static string GenerateMnemonic()
    {
        return string.Join(" ", new Mnemonic(Wordlist.English, WordCount.Twelve).Words);
    }

    public static ECPrivKey DeriveECPrivKeyFromMnemonic(string mnemonic)
    {
        return ECPrivKey.Create(new Mnemonic(mnemonic).DeriveExtKey().PrivateKey.ToBytes());
    }

    /// <summary>
    /// Generates a random ECDSA private key.
    /// </summary>
    public static ECPrivKey GeneratECPrivKey()
    {
        return ECPrivKey.Create(RandomUtils.GetBytes(32));
    }

    /// <summary>
    /// Attempts to obtain a shared public key.
    /// copied from https://github.com/Kukks/NNostr/blob/master/NNostr.Client/Protocols/NIP04.cs
    /// </summary>
    private static bool TryGetSharedPubkey(this ECXOnlyPubKey ecxOnlyPubKey, ECPrivKey key,
        [NotNullWhen(true)] out ECPubKey? sharedPublicKey)
    {
        // 32 + 1 byte for the compression (0x02) prefix.
        Span<byte> input = stackalloc byte[33];
        input[0] = 0x02;
        ecxOnlyPubKey.WriteToSpan(input.Slice(1));

        bool success = Context.Instance.TryCreatePubKey(input, out var publicKey);
        sharedPublicKey = publicKey?.GetSharedPubkey(key);
        return success;
    }

    /// <summary>
    /// Encrypts an object using an ECDSA XOnly public key and a possible ECDSA private key by first computing shared key, and later performing AES Symmetric encryption using shared key.
    /// </summary>
    public static byte[] EncryptObject<T>(T obj, ECXOnlyPubKey theirXPublicKey, ECPrivKey? myPrivKey) where T: IProtoFrame
    {
        byte[]? attachpubKey = null;
        if (myPrivKey == null)
        {
            myPrivKey = GeneratECPrivKey();
            attachpubKey = myPrivKey.CreateXOnlyPubKey().ToBytes();
        }

        if (!TryGetSharedPubkey(theirXPublicKey, myPrivKey, out var sharedKey))
            throw new CryptographicException("Failed to get a shared key.");

        byte[] encryptionKey = sharedKey.ToBytes().AsSpan(1).ToArray();

        byte[] ret = SymmetricObjectEncrypt(encryptionKey, obj);

        if (attachpubKey != null)
            ret = attachpubKey.Concat(ret).ToArray();
        return ret;
    }

    /// <summary>
    /// Decrypts an object using an ECDSA XOnly public key and a possible ECDSA private key by first computing shared key, and later performing AES Symmetric decryption using shared key.
    /// </summary>
    public static T DecryptObject<T>(byte[] encryptedData, ECPrivKey myPrivKey, ECXOnlyPubKey? theirXPublicKey) where T:IProtoFrame
    {
        byte[] encryptedX = encryptedData;
        if (theirXPublicKey == null)
        {
            using (MemoryStream ms = new MemoryStream(encryptedData))
            {
                byte[] pub_key_bytes = new byte[32];
                ms.Read(pub_key_bytes, 0, pub_key_bytes.Length);
                encryptedX = new byte[encryptedData.Length - pub_key_bytes.Length];
                ms.Read(encryptedX, 0, encryptedX.Length);
                theirXPublicKey = ECXOnlyPubKey.Create(pub_key_bytes);
            }
        }

        if (!TryGetSharedPubkey(theirXPublicKey, myPrivKey, out var sharedKey))
            throw new CryptographicException("Failed to get a shared key.");

        byte[] decryptionKey = sharedKey.ToBytes().AsSpan(1).ToArray();

        return SymmetricObjectDecrypt<T>(decryptionKey, encryptedX);
    }

    /// <summary>
    /// Generates Schnorr signature of the SHA256 of serialized object.
    /// </summary>
    public static byte[] SignObject<T>(T obj, ECPrivKey myPrivKey) where T : IProtoFrame
    {
        byte[] serializedObj = BinarySerializeObject<T>(obj);

        Span<byte> buf = stackalloc byte[64];
        using var sha256 = System.Security.Cryptography.SHA256.Create();

        sha256.TryComputeHash(serializedObj, buf, out _);
        myPrivKey.SignBIP340(buf[..32]).WriteToSpan(buf);
        return buf.ToArray();
    }

    /// <summary>
    /// Verifies Schnorr signature of the SHA256 of serialized object. Returns true if the signature is valid.
    /// </summary>
    public static bool VerifyObject<T>(T obj, byte[] signature, ECXOnlyPubKey theirKey) where T : IProtoFrame
    {
        SecpSchnorrSignature sign;
        if (!SecpSchnorrSignature.TryCreate(signature, out sign))
            return false;

        byte[] serializedObj = BinarySerializeObject(obj);

        Span<byte> buf = stackalloc byte[64];
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        sha256.TryComputeHash(serializedObj, buf, out _);

        return theirKey.SigVerifyBIP340(sign, buf);
    }

    /// <summary>
    /// Calculates SHA256 hash for a given byte array.
    /// </summary>
    public static byte[] ComputeSha256(byte[] bytes)
    {
        Span<byte> buf = stackalloc byte[32];
        var sha256 = System.Security.Cryptography.SHA256.Create();
        sha256.TryComputeHash(bytes, buf, out _);
        return buf.ToArray();
    }

    /// <summary>
    /// Calculates SHA256 hash for a list of byte arrays.
    /// </summary>
    public static byte[] ComputeSha256(List<byte[]> items)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            foreach (var item in items)
            {
                sha256.TransformBlock(item, 0, item.Length, null, 0);
            }

            sha256.TransformFinalBlock(new byte[0], 0, 0);

            return sha256.Hash!;
        }
    }

    /// <summary>
    /// Computes a SHA512 hash for a list of byte arrays.
    /// </summary>
    public static byte[] ComputeSha512(List<byte[]> items)
    {
        using (var sha512 = System.Security.Cryptography.SHA512.Create())
        {
            foreach (var item in items)
            {
                sha512.TransformBlock(item, 0, item.Length, null, 0);
            }

            sha512.TransformFinalBlock(new byte[0], 0, 0);

            return sha512.Hash!;
        }
    }

    /// <summary>
    /// Generate a symmetric key for AES encryption.
    /// </summary>
    public static byte[] GenerateSymmetricKey()
    {
        using (Aes aes = Aes.Create())
        {
            aes.GenerateKey();
            return aes.Key;
        }
    }

    /// <summary>
    /// Performs AES symmetric encryption on an object using a symmetric key.
    /// </summary>
    public static byte[] SymmetricObjectEncrypt<T>(byte[] key, T obj) where T : IProtoFrame
    {
        return SymmetricBytesEncrypt(key, BinarySerializeObject(obj));
    }

    public static byte[] SymmetricBytesEncrypt(byte[] key, byte[] serializedObj)
    {
        using (Aes aes = Aes.Create())
        {
            aes.Key = key;
            aes.GenerateIV();

            using (MemoryStream ms = new MemoryStream())
            {
                ms.Write(aes.IV, 0, aes.IV.Length);

                using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(serializedObj, 0, serializedObj.Length);
                    cs.FlushFinalBlock();
                }

                byte[] encryptedObj = ms.ToArray();
                return encryptedObj;
            }
        }
    }

    /// <summary>
    /// Performs AES symmetric decryption on an encrypted object using a symmetric key.
    /// </summary>
    public static T SymmetricObjectDecrypt<T>(byte[] key, byte[] encryptedObj) where T: IProtoFrame
    {
        return BinaryDeserializeObject<T>(SymmetricBytesDecrypt(key, encryptedObj));
    }

    public static byte[] SymmetricBytesDecrypt(byte[] key, byte[] encryptedObj)
    {
        using (Aes aes = Aes.Create())
        {
            aes.Key = key;

            using (MemoryStream ms = new MemoryStream(encryptedObj))
            {
                byte[] iv = new byte[aes.IV.Length];
                ms.Read(iv, 0, iv.Length);
                aes.IV = iv;
                byte[] encryptedData = new byte[encryptedObj.Length - iv.Length];
                ms.Read(encryptedData, 0, encryptedData.Length);

                using (MemoryStream decryptedMs = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(decryptedMs, aes.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(encryptedData, 0, encryptedData.Length);
                        cs.FlushFinalBlock();
                    }
                    return decryptedMs.ToArray();
                }
            }
        }
    }

    public const byte SERIALIZATION_VERSION_BINARY = 0x2;

    public static byte[] BinarySerializeObject<T>(T obj) where T : IProtoFrame
    {
        using var memStream = new MemoryStream();
        memStream.WriteByte(SERIALIZATION_VERSION_BINARY);
        ProtoBuf.Serializer.Serialize(memStream, obj);
        return memStream.ToArray();
    }

    public static object? BinaryDeserializeObject(byte[] data, Type returnType)
    {
        if (data.Length == 0)
            return null;

        using var memStream = new MemoryStream(data);
        var ver = memStream.ReadByte();

        if ( ver != SERIALIZATION_VERSION_BINARY)
            return null;

        return ProtoBuf.Serializer.Deserialize(returnType, memStream);
    }

    /// <summary>
    /// Deserializes a byte array into an object of type T using GZipped Json serialization
    /// </summary>
    public static T? BinaryDeserializeObject<T>(byte[] data) where T : IProtoFrame
    {
        return (T)BinaryDeserializeObject(data, typeof(T));
    }

    public static bool TryParseBitcoinAddress(string text, Network network, out BitcoinAddress? address)
    {
        address = null;

        if (string.IsNullOrEmpty(text) || text.Length > 100)
        {
            return false;
        }

        text = text.Trim();
        try
        {
            address = BitcoinAddress.Create(text, network);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}