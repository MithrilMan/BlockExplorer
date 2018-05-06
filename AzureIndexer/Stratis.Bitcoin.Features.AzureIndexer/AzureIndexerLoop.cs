﻿using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Auth;
using NBitcoin;
using Stratis.Bitcoin.Features.AzureIndexer.IndexTasks;
using Stratis.Bitcoin.Utilities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Stratis.Bitcoin.Features.AzureIndexer
{
    /// <summary>
    /// The AzureIndexerStoreLoop loads blocks from the block repository and indexes them in Azure.
    /// </summary>
    public class AzureIndexerLoop
    {
        /// <summary>Factory for creating background async loop tasks.</summary>
        private readonly IAsyncLoopFactory asyncLoopFactory;

        /// <summary>The async loop we need to wait upon before we can shut down this feature.</summary>
        private IAsyncLoop asyncLoop;

        /// <summary>Another async loop we need to wait upon before we can shut down this feature.</summary>
        private IAsyncLoop asyncLoopChain;

        /// <summary>The full node that owns the block repository that we want to index.</summary>
        public FullNode FullNode { get; }

        /// <summary>Best chain of block headers.</summary>
        internal readonly ConcurrentChain Chain;

        /// <summary>Instance logger.</summary>
        private readonly ILogger logger;

        /// <summary>The node life time let us know when the node is shutting down.</summary>
        private readonly INodeLifetime nodeLifetime;

        /// <summary>The number of blocks to index at a time.</summary>
        private const int IndexBatchSize = 100;

        /// <summary>The name of this node feature for reporting stats.</summary>
        public virtual string StoreName { get { return "AzureIndexer"; } }

        /// <summary>The Azure Indezer settings.</summary>
        private readonly AzureIndexerSettings indexerSettings;

        /// <summary>The highest block that has been indexed.</summary>
        internal ChainedBlock StoreTip { get; private set; }

        /// <summary>The Azure Indexer.</summary>
        public AzureIndexer AzureIndexer { get; private set; }

        public BlockFetcher BlocksFetcher { get; private set; }
        public BlockFetcher TransactionsFetcher { get; private set; }
        public BlockFetcher BalancesFetcher { get; private set; }
        public BlockFetcher WalletsFetcher { get; private set; }

        /// <summary>The Indexer Configuration.</summary>
        public IndexerConfiguration IndexerConfig { get; private set; }


        private readonly ILoggerFactory loggerFactory;

        /// <summary>
        /// Constructs the AzureIndexerLoop.
        /// </summary>
        /// <param name="fullNode">The full node that will be indexed.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public AzureIndexerLoop(FullNode fullNode, ILoggerFactory loggerFactory)
        {
            this.asyncLoopFactory = fullNode.AsyncLoopFactory;
            this.FullNode = fullNode;
            this.Chain = fullNode.Chain;
            this.nodeLifetime = fullNode.NodeLifetime;
            this.indexerSettings = fullNode.NodeService<AzureIndexerSettings>();
            this.loggerFactory = loggerFactory;
            this.logger = loggerFactory.CreateLogger(GetType().FullName);
        }

        /// <summary>
        /// Derives an IndexerConfiguration object from the proviced AzureIndexerSettings object and network.
        /// </summary>
        /// <param name="indexerSettings">The AzureIndexerSettings object to use.</param>
        /// <param name="network">The network to use.</param>
        /// <returns>An IndexerConfiguration object derived from the AzureIndexerSettings object and network.</returns>
        public static IndexerConfiguration IndexerConfigFromSettings(AzureIndexerSettings indexerSettings, Network network, ILoggerFactory loggerFactory)
        {
            IndexerConfiguration indexerConfig = new IndexerConfiguration (loggerFactory)
            {
                StorageNamespace = indexerSettings.StorageNamespace,
                Network = network,
                CheckpointSetName = indexerSettings.CheckpointsetName,
                AzureStorageEmulatorUsed = indexerSettings.AzureEmulatorUsed,
                StorageCredentials = indexerSettings.AzureEmulatorUsed ? null : new StorageCredentials(indexerSettings.AzureAccountName, indexerSettings.AzureKey)
            };
            return indexerConfig;
        }

        /// <summary>
        /// Initializes the Azure Indexer.
        /// </summary>
        public void Initialize()
        {
            this.logger.LogTrace("()");

            this.IndexerConfig = IndexerConfigFromSettings(this.indexerSettings, this.FullNode.Network, this.loggerFactory);

            var indexer = this.IndexerConfig.CreateIndexer();
            indexer.Configuration.EnsureSetup();
            indexer.TaskScheduler = new CustomThreadPoolTaskScheduler(30, 100);
            indexer.CheckpointInterval = this.indexerSettings.CheckpointInterval;
            indexer.IgnoreCheckpoints = this.indexerSettings.IgnoreCheckpoints;
            indexer.FromHeight = this.indexerSettings.From;
            indexer.ToHeight = this.indexerSettings.To;

            this.AzureIndexer = indexer;
            ChainedBlock ckpntBlocks = this.GetCheckPointBlock(IndexerCheckpoints.Blocks);
            ChainedBlock ckpntBalances = this.GetCheckPointBlock(IndexerCheckpoints.Balances);
            ChainedBlock ckpntTransactions = this.GetCheckPointBlock(IndexerCheckpoints.Transactions);
            ChainedBlock ckpntWallets = this.GetCheckPointBlock(IndexerCheckpoints.Wallets);

            if (this.indexerSettings.IgnoreCheckpoints)
                this.SetStoreTip(this.Chain.GetBlock(indexer.FromHeight));
            else
            {
                int minHeight = ckpntBlocks.Height;
                minHeight = Math.Min(minHeight, ckpntBalances.Height);
                minHeight = Math.Min(minHeight, ckpntTransactions.Height);
                minHeight = Math.Min(minHeight, ckpntWallets.Height);

                this.SetStoreTip(this.Chain.GetBlock(minHeight));
            }

            this.BlocksFetcher = this.GetBlockFetcher(IndexerCheckpoints.Blocks, this.nodeLifetime.ApplicationStopping, ckpntBlocks);
            this.BalancesFetcher = this.GetBlockFetcher(IndexerCheckpoints.Balances, this.nodeLifetime.ApplicationStopping, ckpntBalances);
            this.TransactionsFetcher = this.GetBlockFetcher(IndexerCheckpoints.Transactions, this.nodeLifetime.ApplicationStopping, ckpntTransactions);
            this.WalletsFetcher = this.GetBlockFetcher(IndexerCheckpoints.Wallets, this.nodeLifetime.ApplicationStopping, ckpntWallets);

            this.StartLoop();
            
            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Determines the block that a checkpoint is at.
        /// </summary>
        /// <param name="indexerCheckpoints">The type of checkpoint (wallets, blocks, transactions or balances).</param>
        /// <returns>The block that a checkpoint is at.</returns>
        private ChainedBlock GetCheckPointBlock(IndexerCheckpoints indexerCheckpoints)
        {
            this.logger.LogTrace("()");

            Checkpoint checkpoint = this.AzureIndexer.GetCheckpointInternal(indexerCheckpoints);
            ChainedBlock fork = this.Chain.FindFork(checkpoint.BlockLocator);

            this.logger.LogTrace("(-):{0}", fork?.ToString());
            return fork;
        }

        /// <summary>
        /// Starts the indexing loop.
        /// </summary>
        private void StartLoop()
        {
            this.logger.LogTrace("()");

            this.asyncLoop = this.asyncLoopFactory.Run($"{this.StoreName}.IndexAsync", async token =>
            {
                await IndexAsync(this.nodeLifetime.ApplicationStopping);
            },
            this.nodeLifetime.ApplicationStopping,
            repeatEvery: TimeSpans.RunOnce,
            startAfter: TimeSpans.FiveSeconds);

            this.asyncLoopChain = this.asyncLoopFactory.Run($"{this.StoreName}.IndexChainAsync", async token =>
            {
                await IndexChainAsync(this.nodeLifetime.ApplicationStopping);
            },
            this.nodeLifetime.ApplicationStopping,
            repeatEvery: TimeSpans.RunOnce,
            startAfter: TimeSpans.Minute);

            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Shuts down the indexing loop.
        /// </summary>
        public void Shutdown()
        {
            this.logger.LogTrace("()");

            this.asyncLoop.Dispose();

            this.logger.LogTrace("AsyncLoop disposed");

            this.asyncLoopChain.Dispose();

            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Gets a block fetcher that respects the given type of checkpoint.
        /// The block fetcher will return "IndexBatchSize" blocks starting at this.StoreTip + 1.
        /// If "this.AzureIndexer.IgnoreCheckpoints" is set then the checkpoints 
        /// will be ignored by "GetCheckpointInternal".
        /// </summary>
        /// <param name="indexerCheckpoints">The type of checkpoint (wallets, blocks, transactions or balances).</param>
        /// <param name="cancellationToken">The token used for cancellation.</param>
        /// <returns>A block fetcher that respects the given type of checkpoint.</returns>
        private BlockFetcher GetBlockFetcher(IndexerCheckpoints indexerCheckpoints, CancellationToken cancellationToken, ChainedBlock lastProcessed)
        {
            this.logger.LogTrace("()");

            Checkpoint checkpoint = this.AzureIndexer.GetCheckpointInternal(indexerCheckpoints);
            FullNodeBlocksRepository repo = new FullNodeBlocksRepository(this.FullNode);
            var fetcher = new BlockFetcher(checkpoint, repo, this.Chain, lastProcessed, this.loggerFactory)
            {
                NeedSaveInterval = this.indexerSettings.CheckpointInterval,
                FromHeight = this.StoreTip.Height + 1,
                ToHeight = Math.Min(this.StoreTip.Height + IndexBatchSize, this.indexerSettings.To),
                CancellationToken = cancellationToken
            };

            this.logger.LogTrace("(-)");
            return fetcher;
        }

        /// <summary>
        /// Performs "Chain" indexing into Azure storage.
        /// </summary>
        /// <param name="cancellationToken">The token used for cancellation.</param>
        /// <returns>A task for asynchronous completion.</returns>
        private async Task IndexChainAsync(CancellationToken cancellationToken)
        {
            this.logger.LogTrace("()");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {                  
                    this.AzureIndexer.IndexChain(this.Chain, cancellationToken);
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ContinueWith(t => { }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    this.logger.LogTrace("Exception occurred: {0}", ex.ToString());

                    // If something goes wrong then try again 1 minute later
                    IndexerTrace.ErrorWhileImportingBlockToAzure(this.StoreTip.HashBlock, ex);
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ContinueWith(t => { }).ConfigureAwait(false);
                }
            }

            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Performs indexing into Azure storage.
        /// </summary>
        /// <param name="cancellationToken">The token used for cancellation.</param>
        /// <returns>A task for asynchronous completion.</returns>
        private async Task IndexAsync(CancellationToken cancellationToken)
        {
            this.logger.LogTrace("()");

            while (this.StoreTip.Height < indexerSettings.To && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // All indexes will progress more or less in step
                    int fromHeight = this.StoreTip.Height + 1;
                    int toHeight = Math.Min(this.StoreTip.Height + IndexBatchSize, this.indexerSettings.To);

                    this.logger.LogTrace("Starting indexing from {0}, to {1}", fromHeight, toHeight);

                    // Index a batch of blocks
                    if (!cancellationToken.IsCancellationRequested && toHeight > this.BlocksFetcher._LastProcessed.Height)
                    {
                        this.logger.LogTrace("Indexing blocks");

                        this.BlocksFetcher.FromHeight = Math.Max(this.BlocksFetcher._LastProcessed.Height + 1, fromHeight);
                        this.BlocksFetcher.ToHeight = toHeight;
                        var task = new IndexBlocksTask(this.IndexerConfig, this.loggerFactory);
                        task.SaveProgression = !this.indexerSettings.IgnoreCheckpoints;
                        task.Index(this.BlocksFetcher, this.AzureIndexer.TaskScheduler);

                        this.logger.LogTrace("Finished blocks");
                    }

                    // Index a batch of transactions
                    if (!cancellationToken.IsCancellationRequested && toHeight > this.TransactionsFetcher._LastProcessed.Height)
                    {
                        this.logger.LogTrace("Indexing txes");

                        this.TransactionsFetcher.FromHeight = Math.Max(this.TransactionsFetcher._LastProcessed.Height + 1, fromHeight);
                        this.TransactionsFetcher.ToHeight = toHeight;
                        var task = new IndexTransactionsTask(this.IndexerConfig, this.loggerFactory);
                        task.SaveProgression = !this.indexerSettings.IgnoreCheckpoints;
                        task.Index(this.TransactionsFetcher, this.AzureIndexer.TaskScheduler);

                        this.logger.LogTrace("Finished txes");
                    }

                    // Index a batch of balances
                    if (!cancellationToken.IsCancellationRequested && toHeight > this.BalancesFetcher._LastProcessed.Height)
                    {
                        this.logger.LogTrace("Indexing balances");

                        this.BalancesFetcher.FromHeight = Math.Max(this.BalancesFetcher._LastProcessed.Height + 1, fromHeight);
                        this.BalancesFetcher.ToHeight = toHeight;
                        var task = new IndexBalanceTask(this.IndexerConfig, null, this.loggerFactory);
                        task.SaveProgression = !this.indexerSettings.IgnoreCheckpoints;
                        task.Index(this.BalancesFetcher, this.AzureIndexer.TaskScheduler);

                        this.logger.LogTrace("Finished balances");
                    }

                    // Index a batch of wallets
                    if (!cancellationToken.IsCancellationRequested && toHeight > this.WalletsFetcher._LastProcessed.Height)
                    {
                        this.logger.LogTrace("Indexing wallets");

                        this.WalletsFetcher.FromHeight = Math.Max(this.WalletsFetcher._LastProcessed.Height + 1, fromHeight);
                        this.WalletsFetcher.ToHeight = toHeight;
                        var task = new IndexBalanceTask(this.IndexerConfig, this.IndexerConfig.CreateIndexerClient().GetAllWalletRules(), this.loggerFactory);
                        task.SaveProgression = !this.indexerSettings.IgnoreCheckpoints;
                        task.Index(this.WalletsFetcher, this.AzureIndexer.TaskScheduler);

                        this.logger.LogTrace("Finished wallets");
                    }

                    // Update the StoreTip value from the minHeight
                    int minHeight = this.BlocksFetcher._LastProcessed.Height;
                    minHeight = Math.Min(minHeight, this.BalancesFetcher._LastProcessed.Height);
                    minHeight = Math.Min(minHeight, this.TransactionsFetcher._LastProcessed.Height);
                    minHeight = Math.Min(minHeight, this.WalletsFetcher._LastProcessed.Height);

                    this.SetStoreTip(this.Chain.GetBlock(minHeight));
                    this.logger.LogTrace("Indexing iteration finished");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // If something goes wrong then try again 1 minute later
                    this.logger.LogError("Exception occurred: {0}", ex.ToString());
                    IndexerTrace.ErrorWhileImportingBlockToAzure(this.StoreTip.HashBlock, ex);
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ContinueWith(t => { }).ConfigureAwait(false);
                }
            }

            this.logger.LogTrace("(-)");
        }

        /// <summary>
        /// Sets the StoreTip.
        /// </summary>
        /// <param name="chainedBlock">The block to set the store tip to.</param>
        internal void SetStoreTip(ChainedBlock chainedBlock)
        {
            this.logger.LogTrace("({0}:'{1}')", nameof(chainedBlock), chainedBlock?.HashBlock);
            Guard.NotNull(chainedBlock, nameof(chainedBlock));

            this.StoreTip = chainedBlock;

            this.logger.LogTrace("(-)");
        }
    }
}