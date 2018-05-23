using System.Threading.Tasks;
using Google.Protobuf;

namespace AElf.Kernel.KernelAccount
{
    public interface ISmartContractZero : ISmartContract
    {
        Task RegisterSmartContract(SmartContractInvokeContext context, SmartContractRegistration reg);
        Task DeploySmartContract(SmartContractInvokeContext context, SmartContractDeployment smartContractRegister);
        Task<ISmartContract> GetSmartContractAsync(Hash hash);
    }
}