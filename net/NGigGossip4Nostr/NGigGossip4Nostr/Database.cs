﻿using System;
using System.ComponentModel.DataAnnotations;
using CryptoToolkit;
using Microsoft.EntityFrameworkCore;

namespace NGigGossip4Nostr;


/// <summary>
/// Represents a certificate issued for the Subject by Certification Authority.
/// </summary>
public class UserCertificate
{
    /// <summary>
    /// The unique identifier of the certificate.
    /// </summary>
    [Key]
    public Guid CertificateId { get; set; }

    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public required string PublicKey { get; set; }

    /// <summary>
    /// The certificate in byte array format.
    /// </summary>
    public required byte[] TheCertificate { get; set; }
}

public class BroadcastPayloadRow
{
    [Key]
    public Guid AskId { get; set; }

    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public required string PublicKey { get; set; }

    public required byte[] TheBroadcastPayload { get; set; }
}

public class POWBroadcastConditionsFrameRow
{
    [Key]
    public Guid AskId { get; set; }

    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public required string PublicKey { get; set; }

    public required byte[] ThePOWBroadcastConditionsFrame { get; set; }
}

public class BroadcastCounterRow
{
    [Key]
    public Guid PayloadId { get; set; }

    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public required string PublicKey { get; set; }

    public required int Counter { get; set; }
}


public class ReplyPayloadRow
{
    [Key]
    public Guid ReplyId { get; set; }

    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public required string PublicKey { get; set; }

    public Guid PayloadId { get; set; }
    public string ReplierPublicKey { get; set; }
    public byte[] TheReplyPayload { get; set; }
    public string NetworkInvoice { get; set; }
}

public class MonitoredInvoiceRow
{
    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public string PublicKey { get; set; }

    [Key]
    public string PaymentHash { get; set; }
    public string InvoiceState { get; set; }
    public byte[] Data { get; set; }
}

public class MonitoredPreimageRow
{
    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public string PublicKey { get; set; }
    public Uri ServiceUri { get; set; }
    [Key]
    public string PaymentHash { get; set; }
    public string? Preimage { get; set; }
}

public class MonitoredSymmetricKeyRow
{
    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public string PublicKey { get; set; }
    public Uri ServiceUri { get; set; }
    [Key]
    public Guid PayloadId { get; set; }
    public string? SymmetricKey { get; set; }
    public byte[] Data { get; set; }
}

public class MessageDoneRow
{
    /// <summary>
    /// The public key of the subject.
    /// </summary>
    public string PublicKey { get; set; }

    [Key]
    public string MessageId { get; set; }
}

/// <summary>
/// Context class for interaction with database.
/// </summary>
public class GigGossipNodeContext : DbContext
{
    /// <summary>
    /// Connection string to the database.
    /// </summary>
    string connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="WaletContext"/> class.
    /// </summary>
    /// <param name="connectionString">The connection string to connect to the database.</param>
    public GigGossipNodeContext(string connectionString)
    {
        this.connectionString = connectionString;
    }

    public DbSet<UserCertificate> UserCertificates { get; set; }
    public DbSet<BroadcastPayloadRow> BroadcastPayloadsByAskId { get; set; }
    public DbSet<POWBroadcastConditionsFrameRow> POWBroadcastConditionsFrameRowByAskId { get; set; }
    public DbSet<BroadcastCounterRow> BroadcastCounters { get; set; }
    public DbSet<ReplyPayloadRow> ReplyPayloads { get; set; }
    public DbSet<MonitoredInvoiceRow> MonitoredInvoices { get; set; }
    public DbSet<MonitoredPreimageRow> MonitoredPreimages { get; set; }
    public DbSet<MonitoredSymmetricKeyRow> MonitoredSymmetricKeys { get; set; }
    public DbSet<MessageDoneRow> MessagesDone { get; set; }

    /// <summary>
    /// Configures the context for use with a SQLite database.
    /// </summary>
    /// <param name="optionsBuilder">A builder used to create or modify options for this context.</param>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(connectionString).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    dynamic Type2DbSet(object obj)
    {
        if (obj is UserCertificate)
            return this.UserCertificates;
        else if (obj is BroadcastPayloadRow)
            return this.BroadcastPayloadsByAskId;
        else if (obj is POWBroadcastConditionsFrameRow)
            return this.POWBroadcastConditionsFrameRowByAskId;
        else if (obj is BroadcastCounterRow)
            return this.BroadcastCounters;
        else if (obj is ReplyPayloadRow)
            return this.ReplyPayloads;
        else if (obj is MonitoredInvoiceRow)
            return this.MonitoredInvoices;
        else if (obj is MonitoredPreimageRow)
            return this.MonitoredPreimages;
        else if (obj is MonitoredSymmetricKeyRow)
            return this.MonitoredSymmetricKeys;
        else if (obj is MessageDoneRow)
            return this.MessagesDone;

        throw new InvalidOperationException();
    }

    public void SaveObject<T>(T obj)
    {
        this.Type2DbSet(obj).Update(obj);
        this.SaveChanges();
        this.ChangeTracker.Clear();
    }

    public void AddObject<T>(T obj)
    {
        this.Type2DbSet(obj).Add(obj);
        this.SaveChanges();
        this.ChangeTracker.Clear();
    }

    public void AddObjectRange<T>(IEnumerable<T> range)
    {
        if (range.Count() == 0)
            return;
        this.Type2DbSet(range.First()).AddRange(range);
        this.SaveChanges();
        this.ChangeTracker.Clear();
    }

}