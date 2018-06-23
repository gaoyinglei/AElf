﻿﻿using System;
 using System.Collections.Generic;
 using System.Linq;
 using System.Threading;
 using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Extensions;
using AElf.Kernel.KernelAccount;
 using AElf.Sdk.CSharp.Types;
 using Google.Protobuf;
 using Google.Protobuf.WellKnownTypes;
 using SharpRepository.Repository.Configuration;
 using Api = AElf.Sdk.CSharp.Api;
 using CSharpSmartContract = AElf.Sdk.CSharp.CSharpSmartContract;

// ReSharper disable once CheckNamespace
namespace AElf.Kernel.Tests
{
    public class TestContractZero : CSharpSmartContract, ISmartContractZero
    {
        public override async Task InvokeAsync()
        {
            await Task.CompletedTask;
        }

        public async Task<Hash> DeploySmartContract(int category, byte[] contract)
        {
            Console.WriteLine("categort " + category);
            SmartContractRegistration registration = new SmartContractRegistration
            {
                Category = category,
                ContractBytes = ByteString.CopyFrom(contract),
                ContractHash = contract.CalculateHash() // maybe no usage  
            };
            
            var tx = Api.GetTransaction();

            // calculate new account address
            var account = Path.CalculateAccountAddress(tx.From, tx.IncrementId).ToAccount();

            await Api.DeployContractAsync(account, registration);
            Console.WriteLine("Deployment success, contract address: {0}", account.Value.ToBase64());
            return account;
        }

        public void Print(string name)
        {
            Console.WriteLine("Hello, " + name);
        }

        #region DPoS

        private const int MiningTime = 5000;

        private readonly UInt64Field _roundsCount = new UInt64Field("RoundsCount");
        
        private readonly PbField<BlockProducer> _blockProducer = new PbField<BlockProducer>("BPs");
        
        private readonly Map<UInt64Value, RoundInfo> _dPoSInfoMap = new Map<UInt64Value, RoundInfo>("DPoSInfo");
        
        // ReSharper disable once InconsistentNaming
        private readonly Map<UInt64Value, StringValue> _eBPMap = new Map<UInt64Value, StringValue>("EBP");
        
        private readonly PbField<Timestamp> _timeForProducingExtraBlock  = new PbField<Timestamp>("EBTime");
        
        private readonly Map<UInt64Value, StringValue> _firstPlaceMap
            = new Map<UInt64Value, StringValue>("FirstPlaceOfEachRound");
 
        private UInt64Value RoundsCount => new UInt64Value {Value = _roundsCount.GetAsync().Result};
        
        #region Block Producers
        
        public async Task<BlockProducer> GetBlockProducers()
        {
            // Should be setted before
            var blockProducer = await _blockProducer.GetAsync();

            if (blockProducer.Nodes.Count < 1)
            {
                throw new ConfigurationErrorsException("No block producer.");
            }
            
            Api.Return(blockProducer);

            return blockProducer;
        }

        public async Task<BlockProducer> SetBlockProducers(BlockProducer blockProducers)
        {
            await _blockProducer.SetAsync(blockProducers);

            Api.Return(blockProducers);
            
            return blockProducers;
        }
        
        #endregion
        
        #region Genesis block methods
        
        public async Task<DPoSInfo> RandomizeInfoForFirstTwoRounds()
        {
            var blockProducers = await GetBlockProducers();
            var dict = new Dictionary<string, int>();

            // First round
            foreach (var node in blockProducers.Nodes)
            {
                var random = new Random(DateTime.Now.Millisecond + node[0]);
                dict.Add(node, random.Next(0, 1000));
            }

            var sortedMiningNodes =
                from obj in dict
                orderby obj.Value descending
                select obj.Key;

            var enumerable = sortedMiningNodes.ToList();
            
            var infosOfRound1 = new RoundInfo();

            await _roundsCount.SetAsync(1);

            var selected = new Random(DateTime.Now.Millisecond).Next(7, enumerable.Count - 1);
            for (var i = 0; i < enumerable.Count; i++)
            {
                var bpInfo = new BPInfo {IsEBP = false};

                if (i == 0)
                {
                    await _firstPlaceMap.SetValueAsync(RoundsCount, new StringValue {Value = enumerable[0]});
                }
                
                if (i == selected)
                {
                    bpInfo.IsEBP = true;
                    await _eBPMap.SetValueAsync(RoundsCount, new StringValue {Value = enumerable[i]});

                }

                bpInfo.Order = i + 1;
                bpInfo.Signature = Hash.Generate();
                bpInfo.TimeSlot = GetTimestamp(i * MiningTime);

                if (i == enumerable.Count - 1)
                {
                    await _timeForProducingExtraBlock.SetAsync(GetTimestamp(i * MiningTime + MiningTime));
                }

                infosOfRound1.Info.Add(enumerable[i], bpInfo);
            }
            
            await _dPoSInfoMap.SetValueAsync(RoundsCount, infosOfRound1);

            // Second round
            dict = new Dictionary<string, int>();
            
            foreach (var node in blockProducers.Nodes)
            {
                var random = new Random(DateTime.Now.Millisecond + node[1]);
                dict.Add(node, random.Next(0, 1000));
            }
            
            sortedMiningNodes =
                from obj in dict
                orderby obj.Value descending
                select obj.Key;
            
            enumerable = sortedMiningNodes.ToList();
            
            var infosOfRound2 = new RoundInfo();
            
            await _roundsCount.SetAsync(2);
            
            selected = new Random(DateTime.Now.Millisecond).Next(2, enumerable.Count - 1);
            for (var i = 0; i < enumerable.Count; i++)
            {
                var bpInfo = new BPInfo {IsEBP = false};
                
                if (i == 0)
                {
                    await _firstPlaceMap.SetValueAsync(RoundsCount, new StringValue {Value = enumerable[0]});
                }
                
                if (i == selected)
                {
                    bpInfo.IsEBP = true;
                    await _eBPMap.SetValueAsync(RoundsCount, new StringValue {Value = enumerable[i]});
                }

                bpInfo.TimeSlot = GetTimestamp(i * MiningTime);
                bpInfo.Order = i + 1;

                if (i == enumerable.Count - 1)
                {
                    await _timeForProducingExtraBlock.SetAsync(GetTimestamp(i * MiningTime + MiningTime));
                }

                infosOfRound2.Info.Add(enumerable[i], bpInfo);
            }
            
            await _dPoSInfoMap.SetValueAsync(RoundsCount, infosOfRound2);
            
            var dPoSInfo = new DPoSInfo
            {
                RoundInfo = {infosOfRound1, infosOfRound2}
            };
            
            return dPoSInfo;
        }
        
        public async Task<DPoSInfo> GenerateInfoForFirstTwoRounds()
        {
            var blockProducers = await GetBlockProducers();
            var dict = new Dictionary<string, int>();

            // First round
            foreach (var node in blockProducers.Nodes)
            {
                dict.Add(node, node[0]);
            }

            var sortedMiningNodes =
                from obj in dict
                orderby obj.Value descending
                select obj.Key;

            var enumerable = sortedMiningNodes.ToList();
            
            var infosOfRound1 = new RoundInfo();

            await _roundsCount.SetAsync(1);

            var selected = blockProducers.Nodes.Count / 2;
            for (var i = 0; i < enumerable.Count; i++)
            {
                var bpInfo = new BPInfo {IsEBP = false};

                if (i == 0)
                {
                    await _firstPlaceMap.SetValueAsync(new UInt64Value {Value = 1},
                        new StringValue {Value = enumerable[0]});
                }
                
                if (i == selected)
                {
                    bpInfo.IsEBP = true;
                    await _eBPMap.SetValueAsync(new UInt64Value {Value = 1}, new StringValue {Value = enumerable[i]});

                }

                bpInfo.Order = i + 1;
                bpInfo.Signature = Hash.Generate();
                bpInfo.TimeSlot = GetTimestamp(i * MiningTime);

                if (i == enumerable.Count - 1)
                {
                    await _timeForProducingExtraBlock.SetAsync(GetTimestamp(i * MiningTime + MiningTime));
                }

                infosOfRound1.Info.Add(enumerable[i], bpInfo);
            }
            
            await _dPoSInfoMap.SetValueAsync(new UInt64Value {Value = 1}, infosOfRound1);

            // Second round
            dict = new Dictionary<string, int>();
            
            foreach (var node in blockProducers.Nodes)
            {
                dict.Add(node, node[0]);
            }
            
            sortedMiningNodes =
                from obj in dict
                orderby obj.Value descending
                select obj.Key;
            
            enumerable = sortedMiningNodes.ToList();
            
            var infosOfRound2 = new RoundInfo();

            selected = blockProducers.Nodes.Count / 2;
            for (var i = 0; i < enumerable.Count; i++)
            {
                var bpInfo = new BPInfo {IsEBP = false};
                
                if (i == 0)
                {
                    await _firstPlaceMap.SetValueAsync(new UInt64Value {Value = 2}, new StringValue {Value = enumerable[0]});
                }
                
                if (i == selected)
                {
                    bpInfo.IsEBP = true;
                    await _eBPMap.SetValueAsync(new UInt64Value {Value = 2}, new StringValue {Value = enumerable[i]});
                }

                bpInfo.TimeSlot = GetTimestamp(i * MiningTime);
                bpInfo.Order = i + 1;

                if (i == enumerable.Count - 1)
                {
                    await _timeForProducingExtraBlock.SetAsync(GetTimestamp(i * MiningTime + MiningTime));
                }

                infosOfRound2.Info.Add(enumerable[i], bpInfo);
            }
            
            await _dPoSInfoMap.SetValueAsync(new UInt64Value {Value = 2}, infosOfRound2);
            
            var dPoSInfo = new DPoSInfo
            {
                RoundInfo = {infosOfRound1, infosOfRound2}
            };
            
            Api.Return(dPoSInfo);
            
            return dPoSInfo;
        }

        
        #endregion

        #region EBP Methods

        public async Task<RoundInfo> GenerateNextRoundOrder()
        {
            if (RoundsCount.Value == 1)
            {
                return await _dPoSInfoMap.GetValueAsync(RoundsCountAddOne(RoundsCount));
            }
            
            var infosOfNextRound = new RoundInfo();
            var signatureDict = new Dictionary<Hash, string>();
            var orderDict = new Dictionary<int, string>();

            var bpInfo = await GetBlockProducerInfoOfCurrentRound(Api.GetTransaction().From);

            if (!bpInfo.IsEBP)
                return infosOfNextRound;

            var blockProducer = await GetBlockProducers();
            var blockProducerCount = blockProducer.Nodes.Count;

            foreach (var node in blockProducer.Nodes) 
                signatureDict[(await GetBlockProducerInfoOfCurrentRound(node)).Signature] = node;

            foreach (var sig in signatureDict.Keys)
            {
                var sigNum = BitConverter.ToUInt64(
                    BitConverter.IsLittleEndian ? sig.Value.Reverse().ToArray() : sig.Value.ToArray(), 0);
                var order = GetModulus(sigNum, blockProducerCount);

                if (order < 0)
                {
                    order = -order;
                }
 
                if (orderDict.ContainsKey(order))
                {
                    for (var i = 0; i < blockProducerCount; i++)
                    {
                        if (!orderDict.ContainsKey(i))
                        {
                            order = i;
                        }
                    }
                }

                orderDict.Add(order, signatureDict[sig]);
            }

            for (var i = 0; i < orderDict.Count; i++)
            {
                var bpInfoNew = new BPInfo();

                if (i == 0) 
                    await _firstPlaceMap.SetValueAsync(RoundsCountAddOne(RoundsCount), new StringValue {Value = orderDict[0]});

                bpInfoNew.TimeSlot = GetTimestamp(i * MiningTime);
                bpInfoNew.Order = i + 1;

                if (i == orderDict.Count - 1)
                {
                    await _timeForProducingExtraBlock.SetAsync(GetTimestamp(i * MiningTime + MiningTime));
                }
                
                infosOfNextRound.Info[orderDict[i]] = bpInfoNew;
            }

            await _dPoSInfoMap.SetValueAsync(RoundsCountAddOne(RoundsCount), infosOfNextRound);

            return infosOfNextRound;
        }
        
        public async Task<string> GenerateNextRoundOrderDebug()
        {
            var infosOfNextRound = new RoundInfo();
            var signatureDict = new Dictionary<Hash, string>();
            var orderDict = new Dictionary<int, string>();
            var notGivenKey = 0;
            var keyStr = "";
            
            try
            {
                var bpInfo = await GetBlockProducerInfoOfCurrentRound(Api.GetTransaction().From);

                if (!bpInfo.IsEBP)
                    return "no way";

                var blockProducer = await GetBlockProducers();
                var blockProducerCount = blockProducer.Nodes.Count;

                foreach (var node in blockProducer.Nodes) 
                    signatureDict.Add((await GetBlockProducerInfoOfCurrentRound(node)).Signature, node);

                foreach (var sig in signatureDict.Keys)
                {
                    var sigNum = BitConverter.ToUInt64(
                        BitConverter.IsLittleEndian ? sig.Value.Reverse().ToArray() : sig.Value.ToArray(), 0);
                    var order = GetModulus(sigNum, blockProducerCount);

                    if (order < 0)
                    {
                        order = -order;
                    }

                    if (order > 16)
                    {
                        return "what the hell";
                    }
                    
                    if (orderDict.ContainsKey(order))
                    {
                        for (var i = 0; i < blockProducerCount; i++)
                        {
                            if (!orderDict.ContainsKey(i))
                            {
                                order = i;
                            }
                        }
                    }

                    keyStr += order + " ";
                
                    orderDict.Add(order, signatureDict[sig]);
                }

                for (var i = 0; i < orderDict.Count; i++)
                {
                    notGivenKey = i;
                    var bpInfoNew = new BPInfo();

                    if (i == 0) 
                        await _firstPlaceMap.SetValueAsync(RoundsCountAddOne(RoundsCount), new StringValue {Value = orderDict[0]});

                    bpInfoNew.TimeSlot = GetTimestamp(i * MiningTime);
                    bpInfoNew.Order = i + 1;

                    if (i == orderDict.Count - 1)
                    {
                        await _timeForProducingExtraBlock.SetAsync(GetTimestamp(i * MiningTime + MiningTime));
                    }

                    infosOfNextRound.Info.Add(orderDict[i], bpInfoNew);
                }

                await _dPoSInfoMap.SetValueAsync(RoundsCountAddOne(RoundsCount), infosOfNextRound);
            }
            catch (Exception e)
            {
                return $"{e.Message.Replace("key", notGivenKey.ToString())} {keyStr} {orderDict.Count} {signatureDict.Count}";
            }

            return infosOfNextRound.Info.Count.ToString() + orderDict.Count;
        }

        public async Task<string> SetNextExtraBlockProducer()
        {
            var firstPlace = await _firstPlaceMap.GetValueAsync(RoundsCount);
            var firstPlaceInfo = await GetBlockProducerInfoOfCurrentRound(firstPlace.Value);
            var sig = firstPlaceInfo.Signature;
            var sigNum = BitConverter.ToUInt64(
                BitConverter.IsLittleEndian ? sig.Value.Reverse().ToArray() : sig.Value.ToArray(), 0);
            var blockProducer = await GetBlockProducers();
            var blockProducerCount = blockProducer.Nodes.Count;
            var order = GetModulus(sigNum, blockProducerCount);
            
            // ReSharper disable once InconsistentNaming
            var nextEBP = blockProducer.Nodes[order];
            await _eBPMap.SetValueAsync(RoundsCountAddOne(RoundsCount), new StringValue {Value = nextEBP});

            return nextEBP;
        }

        public async Task<UInt64Value> SetRoundsCount()
        {
            var newRoundsCount = RoundsCountAddOne(RoundsCount);
            await _roundsCount.SetAsync(newRoundsCount.Value);

            return newRoundsCount;
        }
        
        public async Task<UInt64Value> GetRoundsCount()
        {
            return new UInt64Value {Value = await _roundsCount.GetAsync()};
        }

        #endregion

        #region BP Methods

        public async Task<BPInfo> PublishOutValueAndSignature(Hash outValue, Hash signature, ulong roundsCount)
        {
            var accountAddress = AddressHashToString(Api.GetTransaction().From);
            var count = roundsCount == 0 ? RoundsCount : new UInt64Value {Value = roundsCount};

            var info = await GetBlockProducerInfoOfSpecificRound(accountAddress, count);
            
            info.OutValue = outValue;
            if (roundsCount > 1)
                info.Signature = signature;
            
            var roundInfo = await _dPoSInfoMap.GetValueAsync(count);
            roundInfo.Info[accountAddress] = info;
            
            await _dPoSInfoMap.SetValueAsync(count, roundInfo);

            return info;
        }
        
        public async Task<BPInfo> PublishOutValueAndSignatureDebug(string outValue, string signature, ulong roundsCount)
        {
            var accountAddress = AddressHashToString(Api.GetTransaction().From);
            var count = roundsCount == 0 ? RoundsCount : new UInt64Value {Value = roundsCount};

            var info = await GetBlockProducerInfoOfSpecificRound(accountAddress, count);
            
            info.OutValue = Base64StringToHash(outValue.Substring(2, outValue.Length - 2));
            if (roundsCount > 1)
                info.Signature = Base64StringToHash(signature.Substring(2, signature.Length - 2));
            
            var roundInfo = await _dPoSInfoMap.GetValueAsync(count);
            roundInfo.Info[accountAddress] = info;
            
            await _dPoSInfoMap.SetValueAsync(count, roundInfo);

            return info;
        }

        public async Task<Hash> TryToPublishInValue(Hash inValue)
        {
            if (!await IsTimeToProduceExtraBlock())
            {
                return null;
            }
            
            var accountAddress = AddressHashToString(Api.GetTransaction().From);
            var info = await GetBlockProducerInfoOfCurrentRound(accountAddress);
            info.InValue = inValue;

            var roundInfo = await _dPoSInfoMap.GetValueAsync(RoundsCount);
            roundInfo.Info[accountAddress] = info;

            await _dPoSInfoMap.SetValueAsync(RoundsCount, roundInfo);

            return inValue;
        }

        #endregion
        
        
        public async Task<Timestamp> GetTimeSlot(string accountAddress = null, ulong roundsCount = 0)
        {
            Interlocked.CompareExchange(ref accountAddress, null,
                AddressHashToString(Api.GetTransaction().From));
            
            roundsCount = roundsCount == 0 ? RoundsCount.Value : roundsCount;
            
            return (await GetBlockProducerInfoOfSpecificRound(accountAddress,
                new UInt64Value {Value = roundsCount})).TimeSlot;
        }

        public async Task<Hash> GetInValueOf(string accountAddress, ulong roundsCount)
        {
            roundsCount = roundsCount == 0 ? RoundsCount.Value : roundsCount;
            return (await GetBlockProducerInfoOfSpecificRound(accountAddress,
                new UInt64Value {Value = roundsCount}))?.InValue;
        }
        
        public async Task<Hash> GetOutValueOf(string accountAddress, ulong roundsCount)
        {
            var count = roundsCount == 0 ? RoundsCount : new UInt64Value {Value = roundsCount};
            return (await GetBlockProducerInfoOfSpecificRound(accountAddress, count))?.OutValue;
        }
        
        public async Task<Hash> GetSignatureOf(string accountAddress, ulong roundsCount)
        {
            var count = roundsCount == 0 ? RoundsCount : new UInt64Value {Value = roundsCount};
            return (await GetBlockProducerInfoOfSpecificRound(accountAddress, count))?.Signature;
        }
        
        public async Task<int?> GetOrderOf(string accountAddress, ulong roundsCount)
        {
            var count = roundsCount == 0 ? RoundsCount : new UInt64Value {Value = roundsCount};
            return (await GetBlockProducerInfoOfSpecificRound(accountAddress, count))?.Order;
        }
        
        public async Task<Hash> CalculateSignature(Hash inValue)
        {
            var add = Hash.Default;
            var blockProducer = await GetBlockProducers();
            foreach (var node in blockProducer.Nodes)
            {
                var bpInfo = await GetBlockProducerInfoOfSpecificRound(node, RoundsCountMinusOne(RoundsCount));
                var lastSignature = bpInfo.Signature;
                add = add.CalculateHashWith(lastSignature);
            }
            
            return inValue.CalculateHashWith(add);
        }
        
        public async Task<bool> AbleToMine()
        {
            var accountHash = Api.GetTransaction().From;
            var accountAddress = AddressHashToString(accountHash);
            var now = GetTimestamp();

            if (!await IsBP(accountAddress))
            {
                return false;
            }
            
            var assignedTimeSlot = await GetTimeSlot(accountAddress);
            var timeSlotEnd = GetTimestamp(assignedTimeSlot, MiningTime);

            return CompareTimestamp(assignedTimeSlot, now) 
                   && CompareTimestamp(timeSlotEnd, now);
        }

        // ReSharper disable once InconsistentNaming
        public async Task<StringValue> GetEBPOf(UInt64Value roundsCount)
        {
            return await _eBPMap.GetValueAsync(roundsCount);
        }
        
        // ReSharper disable once InconsistentNaming
        public async Task<StringValue> GetCurrentEBP()
        {
            return await _eBPMap.GetValueAsync(RoundsCount);
        }
        
        // ReSharper disable once InconsistentNaming
        private async Task<bool> IsBP(string accountAddress)
        {
            var blockProducer = await GetBlockProducers();
            return blockProducer.Nodes.Contains(accountAddress);
        }
        
        // ReSharper disable once InconsistentNaming
        private async Task<bool> IsEBP(string accountAddress)
        {
            var info = await GetBlockProducerInfoOfCurrentRound(accountAddress);
            return info.IsEBP;
        }
        
        public async Task<bool> IsTimeToProduceExtraBlock()
        {
            var expectedTime = await _timeForProducingExtraBlock.GetAsync();
            var now = GetTimestamp();
            return CompareTimestamp(now, expectedTime)
                   && CompareTimestamp(GetTimestamp(expectedTime, MiningTime), now);
        }
        
        public async Task<bool> IsTimeToProduceExtraBlockDebug()
        {
            var expectedTime = await _timeForProducingExtraBlock.GetAsync();
            var now = GetTimestamp();
            return CompareTimestamp(GetTimestamp(), expectedTime)
                   && CompareTimestamp(GetTimestamp(expectedTime, MiningTime), now);
        }

        public async Task<bool> AbleToProduceExtraBlock()
        {
            var accountHash = Api.GetTransaction().From;
            
            // ReSharper disable once InconsistentNaming
            var eBP = await _eBPMap.GetValueAsync(RoundsCount);
            if (AddressHashToString(accountHash) == eBP.Value)
            {
                return false;
            }

            return true;
        }

        // ReSharper disable once InconsistentNaming
        public async Task<StringValue> GetDPoSInfoToString()
        {
            ulong count = 1;
            if (RoundsCount != null)
            {
                count = RoundsCount.Value;
            }
            var result = "";

            ulong i = 1;
            while (i <= count)
            {
                var roundInfoStr = await GetRoundInfoToString(new UInt64Value {Value = i});
                result += $"\n[Round {i}]\n" + roundInfoStr;
                i++;
            }

            return new StringValue {Value = result + "\nCurrent Round : " + RoundsCount?.Value};
        }

        public async Task<string> GetRoundInfoToString(UInt64Value roundsCount)
        {
            var info = await _dPoSInfoMap.GetValueAsync(roundsCount);
            var result = "";

            foreach (var bpInfo in info.Info)
            {
                result += bpInfo.Key + ":\n";
                result += "IsEBP:\t\t" + bpInfo.Value.IsEBP + "\n";
                result += "Order:\t\t" + bpInfo.Value.Order + "\n";
                result += "Timeslot:\t" + bpInfo.Value.TimeSlot.ToDateTime().ToString("u") + "\n";
                result += "Signature:\t" + bpInfo.Value.Signature + "\n";
                result += "Out Value:\t" + bpInfo.Value.OutValue + "\n";
                result += "In Value:\t" + bpInfo.Value.InValue + "\n";
            }

            return result + "\n";
        }

        #region Private Methods

        /// <summary>
        /// Get local time
        /// </summary>
        /// <param name="offset">minutes</param>
        /// <returns></returns>
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private Timestamp GetTimestamp(int offset = 0)
        {
            return Timestamp.FromDateTime(DateTime.UtcNow.AddMilliseconds(offset));
        }

        private Timestamp GetTimestamp(Timestamp origin, int offset)
        {
            return Timestamp.FromDateTime(origin.ToDateTime().AddMilliseconds(offset));
        }

        // ReSharper disable once MemberCanBeMadeStatic.Local
        private bool CompareTimestamp(Timestamp ts1, Timestamp ts2)
        {
            return ts1.ToDateTime() > ts2.ToDateTime();
        }
        
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private UInt64Value RoundsCountAddOne(UInt64Value currentCount)
        {
            var current = currentCount.Value;
            current++;
            return new UInt64Value {Value = current};
        }
        
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private UInt64Value RoundsCountMinusOne(UInt64Value currentCount)
        {
            var current = currentCount.Value;
            current--;
            return new UInt64Value {Value = current};
        }

        private async Task<BPInfo> GetBlockProducerInfoOfSpecificRound(string accountAddress, UInt64Value roundsCount)
        {
            return (await _dPoSInfoMap.GetValueAsync(roundsCount)).Info[accountAddress];
        }
        
        private async Task<BPInfo> GetBlockProducerInfoOfSpecificRound(Hash accountHash, UInt64Value roundsCount)
        {
            return (await _dPoSInfoMap.GetValueAsync(roundsCount)).Info[AddressHashToString(accountHash)];
        }
        
        private async Task<BPInfo> GetBlockProducerInfoOfCurrentRound(string accountAddress)
        {
            return (await _dPoSInfoMap.GetValueAsync(RoundsCount)).Info[accountAddress];
        }
        
        private async Task<BPInfo> GetBlockProducerInfoOfCurrentRound(Hash accountHash)
        {
            return (await _dPoSInfoMap.GetValueAsync(RoundsCount)).Info[AddressHashToString(accountHash)];
        }

        private string AddressHashToString(Hash accountHash)
        {
            return accountHash.ToAccount().Value.ToBase64();
        }

        private Hash Base64StringToHash(string accountAddress)
        {
            return Convert.FromBase64String(accountAddress);
        }

        /// <summary>
        /// In case of forgetting to check negtive value
        /// </summary>
        /// <param name="uLongVal"></param>
        /// <param name="intVal"></param>
        /// <returns></returns>
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private int GetModulus(ulong uLongVal, int intVal)
        {
            var m = (int) uLongVal % intVal;
            if (m < 0)
            {
                m = -m;
            }

            return m;
        }

        #endregion

        #endregion
    }
}
