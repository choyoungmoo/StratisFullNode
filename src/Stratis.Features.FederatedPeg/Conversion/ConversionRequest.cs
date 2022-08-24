﻿using System;
using NBitcoin;
using Stratis.Bitcoin.Features.Wallet;

namespace Stratis.Features.FederatedPeg.Conversion
{
    public enum ConversionRequestType
    {
        Mint,
        Burn
    }

    public enum ConversionRequestStatus
    {
        Unprocessed,
        Submitted, // Unused, need to keep it due to previous state ordering in database
        Processed,

        // States particular to Mint transactions
        OriginatorNotSubmitted,
        OriginatorSubmitted,
        VoteFinalised,
        NotOriginator,
        OriginatorSubmitting,

        Failed,
        Stale, // Set once a request went past max re-org block and was never submitted for processing.
        FailedNoFeeOutput,
        FailedInsufficientFee
    }

    /// <summary>Request to mint or burn wSTRAX.</summary>
    /// <remarks>
    /// When wSTRAX coins are minted and sent to <see cref="DestinationAddress"/> on ETH chain same amount of STRAX coins should be received by the multisig address.
    /// When wSTRAX coins are burned on ETH chain same amount of STRAX coins should be sent to <see cref="DestinationAddress"/>.
    /// </remarks>
    public class ConversionRequest : IBitcoinSerializable
    {
        /// <summary>
        /// The unique identifier for this particular conversion request.
        /// It gets selected by the request creator.
        /// The request ID is typically the initiating transaction ID.
        /// </summary>
        public string RequestId { get { return this.requestId; } set { this.requestId = value; } }

        /// <summary>
        /// The number of the block which included the transaction on the external chain (or Cirrus).
        /// </summary>
        public int ExternalChainBlockHeight { get { return this.externalChainBlockHeight; } set { this.externalChainBlockHeight = value; } }

        /// <summary>
        /// The transaction id on the external chain generated by the submit or confirm transaction action.
        /// <para>A value of 0 indicates unspecified (not yet populated). This was because we added this field in a later release.</para>
        /// </summary>
        public string ExternalChainTxHash { get { return this.externalChainTxHash; } set { this.externalChainTxHash = value; } }

        /// <summary>
        /// The event transaction id returned by calling the contract on the external chain.
        /// </summary>
        public string ExternalChainTxEventId { get { return this.externalChainTxEventId; } set { this.externalChainTxEventId = value; } }

        /// <summary>
        /// The type of the conversion request, mint or burn.
        /// <see cref="ConversionRequestType"/>
        /// </summary>
        public ConversionRequestType RequestType { get { return (ConversionRequestType)this.requestType; } set { this.requestType = (int)value; } }

        /// <summary>
        /// The status of the request, from unprocessed to processed.
        /// </summary>
        public ConversionRequestStatus RequestStatus { get { return (ConversionRequestStatus)this.requestStatus; } set { this.requestStatus = (int)value; } }

        /// <summary>
        /// For a mint request this is needed to coordinate which multisig member is considered the transaction originator on the wallet contract.
        /// A burn request needs to be scheduled for a future block on the main chain so that the conversion can be cleanly inserted into the sequence
        /// of transfers.
        /// </summary>
        public int BlockHeight { get { return this.blockHeight; } set { this.blockHeight = value; } }

        /// <summary>
        /// This could be either:
        /// 1. The Ethereum address to send the minted funds to, or the STRAX address to send unwrapped wSTRAX funds to.
        /// 2. The Cirrus address to send transfers to.
        /// </summary>
        public string DestinationAddress { get { return this.destinationAddress; } set { this.destinationAddress = value; } }

        /// <summary>Chain on which STRAX minting or burning should occur.</summary>
        public DestinationChain DestinationChain { get { return (DestinationChain)this.destinationChain; } set { this.destinationChain = (int)value; } }

        /// <summary>
        /// Amount of the conversion, for wSTRAX conversions this is always denominated in satoshi.
        /// This needs to be converted to wei for submitting wSTRAX mint transactions.
        /// wSTRAX burn transactions are already denominated in wei on the Ethereum chain and thus need to be converted back into satoshi when the
        /// conversion request is created.
        /// For ERC20-SRC20 transfers this amount field is the full-precision integral token amount being transferred, typically 18 decimal places for ERC20.
        /// For ERC721-SRC721 transfers this amount field is the token identifier of the NFT.
        /// </summary>
        public uint256 Amount { get { return this.amount; } set { this.amount = value; } }

        /// <summary>
        /// Indicates whether or not this request has been processed by the interop poller.
        /// </summary>
        public bool Processed { get { return this.processed; } set { this.processed = value; } }

        /// <summary>
        /// Should the request fail, this field can be used for any error messages.
        /// </summary>
        public string StatusMessage { get { return this.statusMessage; } set { this.statusMessage = value; } }

        public string TokenContract { get { return this.tokenContract; } set { this.tokenContract = value; } }

        public string TokenUri { get { return this.tokenUri; } set { this.tokenUri = value; } }

        private uint256 amount;

        private int blockHeight;

        private string destinationAddress;

        private int destinationChain;

        private ulong dummyAmount;

        private int externalChainBlockHeight;

        private string externalChainTxHash;

        private string externalChainTxEventId;

        private bool processed;

        private string requestId;

        private int requestStatus;

        private int requestType;

        private string statusMessage;

        private string tokenContract;

        private string tokenUri;

        public void ReadWrite(BitcoinStream stream)
        {
            stream.ReadWrite(ref this.requestId);
            stream.ReadWrite(ref this.requestType);
            stream.ReadWrite(ref this.requestStatus);
            stream.ReadWrite(ref this.blockHeight);
            stream.ReadWrite(ref this.destinationAddress);

            // This field cannot be removed as it would break the (de)serialisation.
            stream.ReadWrite(ref this.dummyAmount);

            stream.ReadWrite(ref this.processed);

            // All new fields MUST be added to the back.
            ReadWriteNullIntField(stream, ref this.destinationChain);
            ReadWriteNullStringField(stream, ref this.externalChainTxHash);
            ReadWriteNullStringField(stream, ref this.externalChainTxEventId);
            ReadWriteNullStringField(stream, ref this.tokenContract);
            ReadWriteNullStringField(stream, ref this.statusMessage);
            ReadWriteNullIntField(stream, ref this.externalChainBlockHeight);
            ReadWriteNullUInt256Field(stream, ref this.amount);

            // There will be a quantity of conversions that were performed before the introduction of the larger amount field.
            // So we need to transparently substitute the original ulong amount when deserialising.
            if (!stream.Serializing && this.amount == uint256.Zero)
            {
                this.amount = this.dummyAmount;
            }

            ReadWriteNullStringField(stream, ref this.tokenUri);
        }

        private void ReadWriteNullIntField(BitcoinStream stream, ref int nullField)
        {
            if (stream.Serializing)
                stream.ReadWrite(ref nullField);
            else
            {
                try
                {
                    stream.ReadWrite(ref nullField);
                }
                catch (Exception)
                {
                }
            }
        }

        private void ReadWriteNullStringField(BitcoinStream stream, ref string nullField)
        {
            if (stream.Serializing)
            {
                if (string.IsNullOrWhiteSpace(nullField))
                    nullField = "";

                stream.ReadWrite(ref nullField);
            }
            else
            {
                try
                {
                    stream.ReadWrite(ref nullField);
                }
                catch (Exception)
                {
                }
            }
        }

        private void ReadWriteNullUInt256Field(BitcoinStream stream, ref uint256 nullField)
        {
            if (stream.Serializing)
                stream.ReadWrite(ref nullField);
            else
            {
                try
                {
                    stream.ReadWrite(ref nullField);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
