﻿using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Stratis.Bitcoin.Builder;
using Stratis.Bitcoin.Builder.Feature;
using Stratis.Bitcoin.Features.Miner;
using Stratis.Bitcoin.Features.PoA;
using Stratis.Bitcoin.Features.SmartContracts;
using Stratis.Features.Collateral.CounterChain;

namespace Stratis.Features.Collateral
{
    /// <summary>
    /// Sets up the necessary components to check the collateral requirement is met on the counter chain.
    /// </summary>
    public class CollateralFeature : FullNodeFeature
    {
        private readonly ICollateralChecker collateralChecker;
        private readonly IImmatureCollateralKicker immatureCollateralKicker;

        public CollateralFeature(ICollateralChecker collateralChecker, IImmatureCollateralKicker immatureCollateralKicker)
        {
            this.collateralChecker = collateralChecker;
            this.immatureCollateralKicker = immatureCollateralKicker;
        }

        public override async Task InitializeAsync()
        {
            // Note that the node's startup can remain here for a while as it retrieves the collateral for all federation members.
            // This is in contrast with other features' async startup methods that are not required to complete before proceeding.
            await this.collateralChecker.InitializeAsync().ConfigureAwait(false);
            await this.immatureCollateralKicker.InitializeAsync().ConfigureAwait(false);
        }

        public override void Dispose()
        {
            this.immatureCollateralKicker?.Dispose();
            this.collateralChecker?.Dispose();
        }
    }

    /// <summary>
    /// A class providing extension methods for <see cref="IFullNodeBuilder"/>.
    /// </summary>
    public static class FullNodeBuilderCollateralFeatureExtension
    {
        // All Cirrus nodes should call this.
        public static IFullNodeBuilder CheckCollateralCommitment(this IFullNodeBuilder fullNodeBuilder)
        {
            // These rules always execute between all Cirrus nodes.
            fullNodeBuilder.Network.Consensus.ConsensusRules.FullValidationRules.Insert(0, typeof(CheckCollateralCommitmentHeightRule));
            return fullNodeBuilder;
        }

        /// <summary>
        /// Adds mining to the side chain node when on a proof-of-authority network with collateral enabled.
        /// </summary>
        public static IFullNodeBuilder AddPoACollateralMiningCapability<T>(this IFullNodeBuilder fullNodeBuilder) where T : BlockDefinition
        {
            // Inject the CheckCollateralFullValidationRule as the first Full Validation Rule.
            // This is still a bit hacky and we need to properly review the dependencies again between the different side chain nodes.
            fullNodeBuilder.Network.Consensus.ConsensusRules.FullValidationRules.Insert(0, typeof(CheckCollateralFullValidationRule));

            fullNodeBuilder.ConfigureFeature(features =>
            {
                features
                .AddFeature<CollateralFeature>()
                .DependOn<CounterChainFeature>()
                .DependOn<PoAFeature>()
                .FeatureServices(services =>
                {
                    services.AddSingleton<IPoAMiner, CollateralPoAMiner>();
                    services.AddSingleton<MinerSettings>();
                    services.AddSingleton<BlockDefinition, T>();
                    services.AddSingleton<IBlockBufferGenerator, BlockBufferGenerator>();

                    services.AddSingleton<ICollateralChecker, CollateralChecker>();
                    services.AddSingleton<IImmatureCollateralKicker, ImmatureCollateralKicker>();
                });
            });

            return fullNodeBuilder;
        }
    }
}
