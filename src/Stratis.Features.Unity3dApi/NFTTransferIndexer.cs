﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using Stratis.Bitcoin.AsyncWork;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Features.BlockStore.AddressIndexing;
using Stratis.Bitcoin.Features.SmartContracts.Models;
using Stratis.Bitcoin.Features.SmartContracts.Wallet;
using Stratis.Bitcoin.Utilities;
using FileMode = LiteDB.FileMode;

namespace Stratis.Features.Unity3dApi
{
    public interface INFTTransferIndexer : IDisposable
    {
        /// <summary>Initialized NFT indexer.</summary>
        void Initialize();

        /// <summary>Adds NFT contract to watch list. Only contracts from the watch list are being indexed.</summary>
        void WatchNFTContract(string contractAddress);

        /// <summary>Removes NFT contract from watch list.</summary>
        void UnwatchNFTContract(string contractAddress);

        /// <summary>Provides a list of all nft contract addresses that are being tracked.</summary>
        List<string> GetWatchedNFTContracts();

        /// <summary>Provides collection of NFT ids that belong to provided user's address for watched contracts.</summary>
        OwnedNFTsModel GetOwnedNFTs(string address);

        /// <summary>Returns collection of all users that own nft.</summary>
        NFTContractModel GetAllNFTOwnersByContractAddress(string contractAddress);

        /// <summary>Reindexes all tracked contracts.</summary>
        void ReindexAllContracts();
    }

    /// <summary>This component maps addresses to NFT Ids they own.</summary>
    public class NFTTransferIndexer : INFTTransferIndexer
    {
        public ChainedHeader IndexerTip { get; private set; }

        private const string DatabaseFilename = "NFTTransferIndexer.litedb";
        private const string DbOwnedNFTsKey = "OwnedNfts";

        private readonly DataFolder dataFolder;
        private readonly ILogger logger;
        private readonly ChainIndexer chainIndexer;
        private readonly IAsyncProvider asyncProvider;
        private readonly ISmartContractTransactionService smartContractTransactionService;
        private readonly Network network;

        private LiteDatabase db;
        private LiteCollection<NFTContractModel> NFTContractCollection;
        private CancellationTokenSource cancellation;
        private Task indexingTask;

        public NFTTransferIndexer(DataFolder dataFolder, ILoggerFactory loggerFactory, IAsyncProvider asyncProvider,
            ChainIndexer chainIndexer, Network network, ISmartContractTransactionService smartContractTransactionService = null)
        {
            this.network = network;
            this.dataFolder = dataFolder;
            this.cancellation = new CancellationTokenSource();
            this.asyncProvider = asyncProvider;
            this.chainIndexer = chainIndexer;
            this.smartContractTransactionService = smartContractTransactionService;

            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
        }

        /// <inheritdoc />
        public void Initialize()
        {
            if (this.db != null)
                throw new Exception("NFTTransferIndexer already initialized!");

            string dbPath = Path.Combine(this.dataFolder.RootPath, DatabaseFilename);

            FileMode fileMode = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? FileMode.Exclusive : FileMode.Shared;
            this.db = new LiteDatabase(new ConnectionString() { Filename = dbPath, Mode = fileMode });
            this.NFTContractCollection = this.db.GetCollection<NFTContractModel>(DbOwnedNFTsKey);

            this.indexingTask = Task.Run(async () => await this.IndexNFTsContinuouslyAsync().ConfigureAwait(false));
            this.asyncProvider.RegisterTask($"{nameof(AddressIndexer)}.{nameof(this.indexingTask)}", this.indexingTask);

            this.logger.LogDebug("NFTTransferIndexer initialized.");
        }

        private int GetWatchFromHeight()
        {
            int watchFromHeight = this.network.IsTest() ? 3000000 : 3400000;

            return watchFromHeight;
        }

        /// <inheritdoc />
        public void WatchNFTContract(string contractAddress)
        {
            try
            {
                // Check that contract address is a valid address
                var addr = new BitcoinPubKeyAddress(contractAddress, this.network);
            }
            catch (FormatException)
            {
                return;
            }

            int watchFromHeight = this.GetWatchFromHeight();

            if (!this.NFTContractCollection.Exists(x => x.ContractAddress == contractAddress))
            {
                NFTContractModel model = new NFTContractModel()
                {
                    ContractAddress = contractAddress,
                    LastUpdatedBlock = watchFromHeight,
                    OwnedIDsByAddress = new Dictionary<string, HashSet<long>>()
                };

                this.NFTContractCollection.Upsert(model);

                this.logger.LogDebug("Added contract " + contractAddress + " to watchlist.");
            }
            else
                this.logger.LogDebug("Tried to add contract " + contractAddress + " to watchlist, but it's already tracked.");
        }

        /// <inheritdoc />
        public void UnwatchNFTContract(string contractAddress)
        {
            NFTContractModel entryToRemove = this.NFTContractCollection.FindOne(x => x.ContractAddress == contractAddress);

            if (entryToRemove == null)
                return;

            this.NFTContractCollection.Delete(entryToRemove.Id);

            this.logger.LogDebug("Unwatched contract " + contractAddress);
        }

        /// <inheritdoc />
        public List<string> GetWatchedNFTContracts()
        {
            return this.NFTContractCollection.FindAll().Select(x => x.ContractAddress).ToList();
        }

        /// <inheritdoc />
        public OwnedNFTsModel GetOwnedNFTs(string address)
        {
            this.logger.LogDebug("Retrieving owned nfts for address " + address);

            List<NFTContractModel> NFTContractModels = this.NFTContractCollection.FindAll().Where(x => x.OwnedIDsByAddress.ContainsKey(address)).ToList();

            OwnedNFTsModel output = new OwnedNFTsModel() { OwnedIDsByContractAddress = new Dictionary<string, List<long>>() };

            foreach (NFTContractModel contractModel in NFTContractModels)
            {
                HashSet<long> ids = contractModel.OwnedIDsByAddress[address];
                output.OwnedIDsByContractAddress.Add(contractModel.ContractAddress, ids.ToList());
            }

            return output;
        }

        public NFTContractModel GetAllNFTOwnersByContractAddress(string contractAddress)
        {
            this.logger.LogDebug("Retrieving all owned nfts by contract address " + contractAddress);

            NFTContractModel currentContract = this.NFTContractCollection.FindOne(x => x.ContractAddress == contractAddress);
            return currentContract;
        }

        /// <inheritdoc />
        public void ReindexAllContracts()
        {
            this.logger.LogTrace("ReindexAllContracts()");

            int watchFromHeight = this.GetWatchFromHeight();

            foreach (NFTContractModel contractModel in this.NFTContractCollection.FindAll().ToList())
            {
                contractModel.OwnedIDsByAddress = new Dictionary<string, HashSet<long>>();
                contractModel.LastUpdatedBlock = watchFromHeight;

                this.NFTContractCollection.Upsert(contractModel);
            }

            this.logger.LogTrace("ReindexAllContracts(-)");
        }

        private async Task IndexNFTsContinuouslyAsync()
        {
            await Task.Delay(1);

            this.logger.LogDebug("Indexing started");

            try
            {
                while (!this.cancellation.Token.IsCancellationRequested)
                {
                    List<string> contracts = this.NFTContractCollection.FindAll().Select(x => x.ContractAddress).ToList();

                    foreach (string contractAddr in contracts)
                    {
                        if (this.cancellation.Token.IsCancellationRequested)
                            break;

                        this.logger.LogTrace("Updating data for contract: " + contractAddr);

                        NFTContractModel currentContract = this.NFTContractCollection.FindOne(x => x.ContractAddress == contractAddr);

                        ChainedHeader chainTip = this.chainIndexer.Tip;

                        if (chainTip.Height == currentContract.LastUpdatedBlock)
                        {
                            this.logger.LogTrace("No need to update, already up to tip.");
                            continue;
                        }

                        List<ReceiptResponse> receipts = this.smartContractTransactionService.ReceiptSearch(
                            contractAddr, "TransferLog", null, currentContract.LastUpdatedBlock + 1, null);

                        if ((receipts == null) || (receipts.Count == 0))
                        {
                            currentContract.LastUpdatedBlock = chainTip.Height;
                            this.NFTContractCollection.Upsert(currentContract);

                            this.logger.LogTrace("No receipts found. Updated to height " + chainTip.Height);
                            continue;
                        }

                        int lastReceiptHeight = 0;
                        if (receipts.Any())
                            lastReceiptHeight = (int)receipts.Last().BlockNumber.Value;

                        currentContract.LastUpdatedBlock = new List<int>() { chainTip.Height, lastReceiptHeight }.Max();

                        List<TransferLog> transferLogs = new List<TransferLog>(receipts.Count);

                        foreach (ReceiptResponse receiptRes in receipts)
                        {
                            LogData log = receiptRes.Logs.First().Log;
                            string jsonLog = JsonConvert.SerializeObject(log);

                            TransferLogRoot infoObj = JsonConvert.DeserializeObject<TransferLogRoot>(jsonLog);
                            transferLogs.Add(infoObj.Data);
                        }

                        this.logger.LogDebug("Processing transafer logs.");

                        for (int i = 0; i < transferLogs.Count; i++)
                        {
                            TransferLog transferInfo = transferLogs[i];

                            this.logger.LogDebug("log #{0}: From: {1} To: {2} Id:{3}", i, transferInfo.From, transferInfo.To, transferInfo.TokenId);

                            if ((transferInfo.From != null) && currentContract.OwnedIDsByAddress.ContainsKey(transferInfo.From))
                            {
                                bool fromExists = currentContract.OwnedIDsByAddress.ContainsKey(transferInfo.From);

                                this.logger.LogDebug("FromExists: {0} ",  fromExists);

                                currentContract.OwnedIDsByAddress[transferInfo.From].Remove(transferInfo.TokenId);

                                if (currentContract.OwnedIDsByAddress[transferInfo.From].Count == 0)
                                    currentContract.OwnedIDsByAddress.Remove(transferInfo.From);
                            }

                            if (!currentContract.OwnedIDsByAddress.ContainsKey(transferInfo.To))
                            {
                                this.logger.LogDebug("Added ID to To");
                                currentContract.OwnedIDsByAddress.Add(transferInfo.To, new HashSet<long>());
                            }
                            else
                                this.logger.LogDebug("Already added!");

                            currentContract.OwnedIDsByAddress[transferInfo.To].Add(transferInfo.TokenId);
                        }

                        this.NFTContractCollection.Upsert(currentContract);

                        this.logger.LogTrace("Found " + transferLogs.Count + " transfer logs. Last updated block: " + currentContract.LastUpdatedBlock);
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), this.cancellation.Token);
                    }
                    catch (TaskCanceledException)
                    {
                    }
                }
            }
            catch (Exception e)
            {
                this.logger.LogError(e.ToString());
            }

            this.logger.LogDebug("Indexing stopped");
        }

        public void Dispose()
        {
            this.logger.LogDebug("Dispose()");

            this.cancellation.Cancel();
            this.indexingTask?.GetAwaiter().GetResult();
            this.db?.Dispose();

            this.logger.LogDebug("Dispose(-)");
        }
    }

    public class NFTContractModel
    {
        public int Id { get; set; }

        public string ContractAddress { get; set; }

        // Key is nft owner address, value is list of NFT IDs
        public Dictionary<string, HashSet<long>> OwnedIDsByAddress { get; set; }

        public int LastUpdatedBlock { get; set; }
    }

    public class OwnedNFTsModel
    {
        public Dictionary<string, List<long>> OwnedIDsByContractAddress { get; set; }
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.4.3.0 (Newtonsoft.Json v11.0.0.0)")]
    public partial class TransferLogRoot
    {
        public string Event { get; set; }
        public TransferLog Data { get; set; }
    }

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "10.4.3.0 (Newtonsoft.Json v11.0.0.0)")]
    public partial class TransferLog
    {
        [JsonProperty("From", Required = Required.DisallowNull, NullValueHandling = NullValueHandling.Ignore)]
        public string From { get; set; }

        [JsonProperty("To", Required = Required.DisallowNull, NullValueHandling = NullValueHandling.Ignore)]
        public string To { get; set; }

        [JsonProperty("TokenId", Required = Required.DisallowNull, NullValueHandling = NullValueHandling.Ignore)]
        public long TokenId { get; set; }
    }
}
