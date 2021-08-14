//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.FeeHistoryOracleTests;
using static Nethermind.JsonRpc.Test.Modules.GasPriceOracleTests;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [TestCase("2", "earliest", "[0,10.5,20,60,90]", "\"result\":{\"oldestBlock\":\"0x0\",\"reward\":[[\"0x1\",\"0x1\",\"0x1\",\"0x2\",\"0x2\"]],\"baseFeePerGas\":[\"0x2\",\"0x2\"],\"gasUsedRatio\":[0.7]}")]
        [TestCase("1", "latest", "[0,10.5,20,60,90]", "\"result\":{\"oldestBlock\":\"0x1\",\"reward\":[[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x3\"]],\"baseFeePerGas\":[\"0x3\",\"0x3\"],\"gasUsedRatio\":[0.5]}")]
        [TestCase("1", "pending", "[0,10.5,20,60,90]", "\"error\":{\"code\":-32002,\"message\":\"newestBlock: Block is not available\"}")]
        [TestCase("2", "0x01", "[0,10.5,20,60,90]", "\"result\":{\"oldestBlock\":\"0x0\",\"reward\":[[\"0x1\",\"0x1\",\"0x1\",\"0x2\",\"0x2\"],[\"0x0\",\"0x0\",\"0x0\",\"0x0\",\"0x3\"]],\"baseFeePerGas\":[\"0x2\",\"0x3\",\"0x3\"],\"gasUsedRatio\":[0.7,0.5]}")]
        public async Task Eth_feeHistory(string blockCount, string blockParameter, string rewardPercentiles,
            string expectedResult)
        {
            using Context ctx = await Context.Create();
            FeeHistoryOracle feeHistoryOracle = GetTestFeeHistoryOracle();
            ctx._test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithFeeHistoryOracle(feeHistoryOracle).Build();
            string serialized = ctx._test.TestEthRpc("eth_feeHistory", blockCount, blockParameter, rewardPercentiles);
            serialized.Should().Be($"{{\"jsonrpc\":\"2.0\",{expectedResult},\"id\":67}}");
        }

        public FeeHistoryOracle GetTestFeeHistoryOracle()
        {
            Transaction tx1FirstBlock = Build.A.Transaction.WithGasPrice(3).TestObject; //Reward: Min (3, 3-2) => 1 
            Transaction tx2FirstBlock = Build.A.Transaction.WithGasPrice(4).TestObject; //Reward: Min (4, 4-2) => 2
            // Gas Used and Reward: [(2,1), (5,2)]
            // Percentile Thresholds: [0, 0, 1, 4, 6]
            // Percentile Rewards: [1, 1, 1, 2, 2]
            Transaction tx1SecondBlock = Build.A.Transaction.WithMaxPriorityFeePerGas(5).WithMaxFeePerGas(6).WithType(TxType.EIP1559).TestObject; //Reward: Min (6 - 3, 5) => 3
            Transaction tx2SecondBlock = Build.A.Transaction.WithMaxPriorityFeePerGas(2).WithMaxFeePerGas(2).WithType(TxType.EIP1559).TestObject; //Reward: BaseFee (3) > FeeCap (2) => 0
            // Gas Used and Reward: [(3,0), (2,3)]
            // Percentile Thresholds: [0, 0, 1, 3, 4]
            // Percentile Rewards: [0, 0, 0, 0, 3]
            Block firstBlock = Build.A.Block.
                Genesis.
                WithBaseFeePerGas(2).
                WithGasUsed(7). //Todo fix gas Used
                WithGasLimit(10).
                WithTransactions(tx1FirstBlock, tx2FirstBlock).
                TestObject;
            Block secondBlock = Build.A.Block.
                WithNumber(1).
                WithBaseFeePerGas(3).
                WithGasUsed(5).
                WithGasLimit(10).
                WithTransactions(tx1SecondBlock, tx2SecondBlock).
                TestObject;
            
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            BlockParameter newestBlockParameter = new(1) {RequireCanonical = true};
            blockFinder.FindBlock(newestBlockParameter).Returns(secondBlock);
            blockFinder.FindParent(secondBlock, BlockTreeLookupOptions.RequireCanonical).Returns(firstBlock);
            BlockParameter blockParameterEarliest = new(BlockParameterType.Earliest) {RequireCanonical = true};
            BlockParameter blockParameterLatest = new(BlockParameterType.Latest) {RequireCanonical = true};
            BlockParameter blockParameterPending = new(BlockParameterType.Pending) {RequireCanonical = true};
            blockFinder.FindBlock(blockParameterEarliest).Returns(firstBlock);
            blockFinder.FindBlock(blockParameterLatest).Returns(secondBlock);
            blockFinder.FindBlock(blockParameterPending).Returns((Block?) null);
            
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            receiptStorage.Get(firstBlock).Returns(new TxReceipt[]
            {
                new() {GasUsed = 2},
                new() {GasUsed = 5}
            });
            receiptStorage.Get(secondBlock).Returns(new TxReceipt[]
            {
                new() {GasUsed = 2},
                new() {GasUsed = 3}
            });

            IReleaseSpec eip1559EnabledReleaseSpec = Substitute.For<IReleaseSpec>();
            eip1559EnabledReleaseSpec.IsEip1559Enabled.Returns(true);
            IReleaseSpec eip1559NotEnabledReleaseSpec = Substitute.For<IReleaseSpec>();
            eip1559NotEnabledReleaseSpec.IsEip1559Enabled.Returns(false);
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Is<long>(l => l == 2)).Returns(eip1559EnabledReleaseSpec);
            specProvider.GetSpec(Arg.Is<long>(l => l == 1)).Returns(eip1559NotEnabledReleaseSpec);
            FeeHistoryOracle feeHistoryOracle =
                GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder, receiptStorage: receiptStorage, specProvider: specProvider);

            return feeHistoryOracle;
        }
    }
}
