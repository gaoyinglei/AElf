﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.ChainController;
using AElf.Kernel.Types;
using AElf.Configuration;
using AElf.Cryptography.ECDSA;
using AElf.Kernel.Consensus;
using AElf.SmartContract;
using AElf.Types.CSharp;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NLog;

namespace AElf.Kernel.Node
{
    public class Consensus
    {
        private readonly ILogger _logger;
        private bool IsMining { get; set; }

        private ECKeyPair NodeKeyPair
        {
            get => Node.NodeKeyPair;
        }

        private readonly INodeConfig _nodeConfig;
        private readonly IAccountContextService _accountContextService;
        private readonly ITxPoolService _txPoolService;
        
        // ReSharper disable once InconsistentNaming
        public AElfDPoSHelper DPoSHelper { get; }
        private MainChainNode Node { get; }
        private readonly Stack<Hash> _consensusData = new Stack<Hash>();
        private bool _incrementIdNeedToAddOne;

        // ReSharper disable once InconsistentNaming
        public AElfDPoSObserver AElfDPoSObserver => new AElfDPoSObserver(_logger,
            MiningWithInitializingAElfDPoSInformation,
            MiningWithPublishingOutValueAndSignature, PublishInValue, MiningWithUpdatingAElfDPoSInformation);

        public Consensus(ILogger logger, MainChainNode node, INodeConfig nodeConfig,
            IWorldStateDictator worldStateDictator, IAccountContextService accountContextService, ITxPoolService txPoolService)
        {
            _logger = logger;
            _nodeConfig = nodeConfig;
            _accountContextService = accountContextService;
            _txPoolService = txPoolService;
            Node = node;
            DPoSHelper = new AElfDPoSHelper(worldStateDictator, NodeKeyPair, nodeConfig.ChainId, BlockProducers,
               Node.ContractAccountHash, _logger);
        }

        public BlockProducer BlockProducers
        {
            get
            {
                var dict = MinersConfig.Instance.Producers;
                var blockProducers = new BlockProducer();

                foreach (var bp in dict.Values)
                {
                    var b = bp["address"].RemoveHexPrefix();
                    blockProducers.Nodes.Add(b);
                }

                Globals.BlockProducerNumber = blockProducers.Nodes.Count;
                return blockProducers;
            }
        }

        /// <summary>
        /// temple mine to generate fake block data with loop
        /// </summary>
        public async void StartConsensusProcess()
        {
            if (IsMining)
                return;

            IsMining = true;

            switch (Globals.ConsensusType)
            {
                case ConsensusType.AElfDPoS:
                    if (!BlockProducers.Nodes.Contains(NodeKeyPair.GetAddress().ToHex().RemoveHexPrefix()))
                    {
                        break;
                    }

                    if (_nodeConfig.ConsensusInfoGenerater && !await DPoSHelper.HasGenerated())
                    {
                        AElfDPoSObserver.Initialization();
                        break;
                    }
                    else
                    {
                        DPoSHelper.SyncMiningInterval();
                        _logger?.Trace($"Set AElf DPoS mining interval: {Globals.AElfDPoSMiningInterval} ms.");
                    }

                    if (DPoSHelper.CanRecoverDPoSInformation())
                    {
                        AElfDPoSObserver.RecoverMining();
                    }

                    break;

                case ConsensusType.PoTC:
                    await Node.Mine();
                    break;

                case ConsensusType.SingleNode:
                    Node.SingleNodeTestProcess();
                    break;
            }
        }

        // ReSharper disable once InconsistentNaming
        public async Task MiningWithInitializingAElfDPoSInformation()
        {
            var parameters = new List<byte[]>
            {
                BlockProducers.ToByteArray(),
                DPoSHelper.GenerateInfoForFirstTwoRounds().ToByteArray(),
                new Int32Value {Value = Globals.AElfDPoSMiningInterval}.ToByteArray()
            };
            _logger?.Trace($"Set AElf DPoS mining interval: {Globals.AElfDPoSMiningInterval} ms");
            // ReSharper disable once InconsistentNaming
            var txToInitializeAElfDPoS = GenerateTransaction("InitializeAElfDPoS", parameters);
            await Node.BroadcastTransaction(txToInitializeAElfDPoS);

            var block = await Node.Mine();
            await Node.BroadcastBlock(block);
        }

        /// <summary>
        /// return default incrementId for one address
        /// </summary>
        /// <param name="addr"></param>
        /// <returns></returns>
        public async Task<ulong> GetIncrementId(Hash addr)
        {
            try
            {
                bool isDPoS = addr.Equals(NodeKeyPair.GetAddress()) ||
                              DPoSHelper.BlockProducer.Nodes.Contains(addr.ToHex().RemoveHexPrefix());
                
                // ReSharper disable once InconsistentNaming
                var idInDB = (await _accountContextService.GetAccountDataContext(addr, _nodeConfig.ChainId)).IncrementId;
                _logger?.Log(LogLevel.Debug, $"Trying to get increment id, {isDPoS}");
                var idInPool = _txPoolService.GetIncrementId(addr, isDPoS);
                _logger?.Log(LogLevel.Debug, $"End Trying to get increment id, {isDPoS}");

                return Math.Max(idInDB, idInPool);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Failed to get increment id.");
                return 0;
            }
        }
        
        // ReSharper disable once InconsistentNaming
        private ITransaction GenerateTransaction(string methodName, IReadOnlyList<byte[]> parameters,
            ulong incrementIdOffset = 0)
        {
            var tx = new Transaction
            {
                From = NodeKeyPair.GetAddress(),
                To = Node.ContractAccountHash,
                IncrementId = GetIncrementId(NodeKeyPair.GetAddress()).Result + incrementIdOffset,
                MethodName = methodName,
                P = ByteString.CopyFrom(NodeKeyPair.PublicKey.Q.GetEncoded()),
                Type = TransactionType.DposTransaction
            };

            switch (parameters.Count)
            {
                case 2:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1]));
                    break;
                case 3:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1], parameters[2]));
                    break;
                case 4:
                    tx.Params = ByteString.CopyFrom(ParamsPacker.Pack(parameters[0], parameters[1], parameters[2],
                        parameters[3]));
                    break;
            }

            var signer = new ECSigner();
            var signature = signer.Sign(NodeKeyPair, tx.GetHash().GetHashBytes());

            // Update the signature
            tx.R = ByteString.CopyFrom(signature.R);
            tx.S = ByteString.CopyFrom(signature.S);

            return tx;
        }

        public async Task MiningWithPublishingOutValueAndSignature()
        {
            var inValue = Hash.Generate();
            if (_consensusData.Count <= 0)
            {
                _consensusData.Push(inValue.CalculateHash());
                _consensusData.Push(inValue);
            }

            var currentRoundNumber = DPoSHelper.CurrentRoundNumber;
            var signature = Hash.Default;
            if (currentRoundNumber.Value > 1)
            {
                signature = DPoSHelper.CalculateSignature(inValue);
            }

            var parameters = new List<byte[]>
            {
                DPoSHelper.CurrentRoundNumber.ToByteArray(),
                new StringValue {Value = NodeKeyPair.GetAddress().ToHex().RemoveHexPrefix()}.ToByteArray(),
                _consensusData.Pop().ToByteArray(),
                signature.ToByteArray()
            };

            var txToPublishOutValueAndSignature = GenerateTransaction("PublishOutValueAndSignature", parameters);

            await Node.BroadcastTransaction(txToPublishOutValueAndSignature);

            var block = await Node.Mine();
            await Node.BroadcastBlock(block);
        }

        public async Task PublishInValue()
        {
            if (_consensusData.Count <= 0)
            {
                _incrementIdNeedToAddOne = false;
                return;
            }

            _incrementIdNeedToAddOne = true;

            var currentRoundNumber = DPoSHelper.CurrentRoundNumber;

            var parameters = new List<byte[]>
            {
                currentRoundNumber.ToByteArray(),
                new StringValue {Value = NodeKeyPair.GetAddress().ToHex().RemoveHexPrefix()}.ToByteArray(),
                _consensusData.Pop().ToByteArray()
            };

            var txToPublishInValue = GenerateTransaction("PublishInValue", parameters);
            await Node.BroadcastTransaction(txToPublishInValue);
        }
        
        // ReSharper disable once InconsistentNaming
        public async Task MiningWithUpdatingAElfDPoSInformation()
        {
            _logger?.Log(LogLevel.Debug, "MiningWithUpdatingAElf..");
            var extraBlockResult = await DPoSHelper.ExecuteTxsForExtraBlock();
            _logger?.Log(LogLevel.Debug, "End MiningWithUpdatingAElf..");

            var parameters = new List<byte[]>
            {
                extraBlockResult.Item1.ToByteArray(),
                extraBlockResult.Item2.ToByteArray(),
                extraBlockResult.Item3.ToByteArray()
            };
            _logger?.Log(LogLevel.Debug, "Generating transaction..");

            var txForExtraBlock = GenerateTransaction(
                "UpdateAElfDPoS",
                parameters,
                _incrementIdNeedToAddOne ? (ulong) 1 : 0);
            _logger?.Log(LogLevel.Debug, "End Generating transaction..");

            await Node.BroadcastTransaction(txForExtraBlock);

            var block = await Node.Mine();
            await Node.BroadcastBlock(block);
        }
    }
}