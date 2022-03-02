using OpenHardwareMonitor.Hardware;
using System;
using System.IO;

namespace ZenStates.Core
{
    public class Cpu : IDisposable
    {
        private bool disposedValue;
        private const string InitializationExceptionText = "CPU module initialization failed.";

        public const uint F17H_M01H_SVI = 0x0005A000;
        public const uint F17H_M60H_SVI = 0x0006F000; // Renoir only?
        public const uint F17H_M01H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0xC);
        public const uint F17H_M01H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0x10);
        public const uint F17H_M30H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0x14);
        public const uint F17H_M30H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0x10);
        public const uint F17H_M60H_SVI_TEL_PLANE0 = (F17H_M60H_SVI + 0x38);
        public const uint F17H_M60H_SVI_TEL_PLANE1 = (F17H_M60H_SVI + 0x3C);
        public const uint F17H_M70H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0x10);
        public const uint F17H_M70H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0xC);
        public const uint F19H_M21H_SVI_TEL_PLANE0 = (F17H_M01H_SVI + 0x10);
        public const uint F19H_M21H_SVI_TEL_PLANE1 = (F17H_M01H_SVI + 0xC);

        public enum Family
        {
            UNSUPPORTED = 0x0,
            FAMILY_15H = 0x15,
            FAMILY_17H = 0x17,
            FAMILY_18H = 0x18,
            FAMILY_19H = 0x19,
        };

        public enum CodeName
        {
            Unsupported = 0,
            DEBUG,
            BristolRidge,
            SummitRidge,
            Whitehaven,
            Naples,
            RavenRidge,
            PinnacleRidge,
            Colfax,
            Picasso,
            FireFlight,
            Matisse,
            CastlePeak,
            Rome,
            Dali,
            Renoir,
            VanGogh,
            Vermeer,
            Chagall,
            Milan,
            Cezanne,
            Rembrandt,
            Lucienne,
        };


        // CPUID_Fn80000001_EBX [BrandId Identifier] (BrandId)
        // [31:28] PkgType: package type.
        // Socket FP5/FP6 = 0
        // Socket AM4 = 2
        // Socket SP3 = 4
        // Socket TR4/TRX4 (SP3r2/SP3r3) = 7
        public enum PackageType
        {
            FPX = 0,
            AM4 = 2,
            SP3 = 4,
            TRX = 7,
        }

        public struct SVI2
        {
            public uint coreAddress;
            public uint socAddress;
        }

        public struct CPUInfo
        {
            public uint cpuid;
            public Family family;
            public CodeName codeName;
            public string cpuName;
            public PackageType packageType;
            public uint baseModel;
            public uint extModel;
            public uint model;
            public uint ccds;
            public uint ccxs;
            public uint coresPerCcx;
            public uint cores;
            public uint logicalCores;
            public uint physicalCores;
            public uint threadsPerCore;
            public uint cpuNodes;
            public uint patchLevel;
            public uint coreDisableMap;
            public SVI2 svi2;
        }

        public readonly IOModule io = new IOModule();
        public readonly CPUInfo info;
        public readonly SystemInfo systemInfo;
        public readonly SMU smu;
        public readonly PowerTable powerTable;

        public IOModule.LibStatus Status { get; }
        public Exception LastError { get; }

        public Cpu()
        {
            Ring0.Open();

            if (!Ring0.IsOpen)
            {
                string errorReport = Ring0.GetReport();
                using (var sw = new StreamWriter("WinRing0.txt", true))
                {
                    sw.Write(errorReport);
                }

                throw new ApplicationException("Error opening WinRing kernel driver");
            }

            Opcode.Open();

            uint ccdsPresent = 0, ccdsDown = 0, coreFuse = 0;
            uint fuse1 = 0x5D218;
            uint fuse2 = 0x5D21C;
            uint offset = 0x238;
            uint ccxPerCcd = 2;

            if (Opcode.Cpuid(0x00000001, 0, out uint eax, out uint ebx, out uint ecx, out uint edx))
            {
                info.cpuid = eax;
                info.family = (Family)(((eax & 0xf00) >> 8) + ((eax & 0xff00000) >> 20));
                info.baseModel = (eax & 0xf0) >> 4;
                info.extModel = (eax & 0xf0000) >> 12;
                info.model = info.baseModel + info.extModel;
                info.logicalCores = Utils.GetBits(ebx, 16, 8);
            }
            else
            {
                throw new ApplicationException(InitializationExceptionText);
            }

            // Package type
            if (Opcode.Cpuid(0x80000001, 0, out eax, out ebx, out ecx, out edx))
            {
                info.packageType = (PackageType)(ebx >> 28);
                info.codeName = GetCodeName(info);
                smu = GetMaintainedSettings.GetByType(info.codeName);
                smu.Version = GetSmuVersion();
                smu.TableVersion = GetTableVersion();
            }
            else
            {
                throw new ApplicationException(InitializationExceptionText);
            }

            info.cpuName = GetCpuName();

            if (Opcode.Cpuid(0x8000001E, 0, out eax, out ebx, out ecx, out edx))
            {
                info.threadsPerCore = Utils.GetBits(ebx, 8, 4) + 1;
                info.cpuNodes = ecx >> 8 & 0x7 + 1;

                if (info.threadsPerCore == 0)
                    info.cores = info.logicalCores;
                else
                    info.cores = info.logicalCores / info.threadsPerCore;
            }
            else
            {
                throw new ApplicationException(InitializationExceptionText);
            }

            // Non-critical block
            try
            {
                // Get CCD and CCX configuration
                // https://gitlab.com/leogx9r/ryzen_smu/-/blob/master/userspace/monitor_cpu.c
                if (info.family == Family.FAMILY_19H)
                {
                    offset = 0x598;
                    ccxPerCcd = 1;
                }
                else if (info.family == Family.FAMILY_17H && info.model != 0x71 && info.model != 0x31)
                {
                    fuse1 += 0x40;
                    fuse2 += 0x40;
                }

                if (!ReadDwordEx(fuse1, ref ccdsPresent) || !ReadDwordEx(fuse2, ref ccdsDown))
                    throw new ApplicationException("Could not read CCD fuse!");

                uint ccdEnableMap = Utils.GetBits(ccdsPresent, 22, 8);
                uint ccdDisableMap = Utils.GetBits(ccdsPresent, 30, 2) | (Utils.GetBits(ccdsDown, 0, 6) << 2);
                uint coreDisableMapAddress = 0x30081800 + offset;

                info.ccds = Utils.CountSetBits(ccdEnableMap);
                info.ccxs = info.ccds * ccxPerCcd;
                info.physicalCores = info.ccxs * 8 / ccxPerCcd;

                if (ReadDwordEx(coreDisableMapAddress, ref coreFuse))
                    info.coresPerCcx = (8 - Utils.CountSetBits(coreFuse & 0xff)) / ccxPerCcd;
                else
                    throw new ApplicationException("Could not read core fuse!");

                uint ccdOffset = 0;

                for (int i = 0; i < info.ccds; i++)
                {
                    if (Utils.GetBits(ccdEnableMap, i, 1) == 1)
                    {
                        if (ReadDwordEx(coreDisableMapAddress | ccdOffset, ref coreFuse))
                            info.coreDisableMap |= (coreFuse & 0xff) << i * 8;
                        else
                            throw new ApplicationException($"Could not read core fuse for CCD{i}!");
                    }

                    ccdOffset += 0x2000000;
                }
            }
            catch (Exception ex)
            {
                LastError = ex;
                Status = IOModule.LibStatus.PARTIALLY_OK;
            }

            try
            {
                info.patchLevel = GetPatchLevel();
                info.svi2 = GetSVI2Info(info.codeName);
                systemInfo = new SystemInfo(info, smu);
                powerTable = new PowerTable(smu.TableVersion, smu.SMU_TYPE, GetDramBaseAddress());

                if (!SendTestMessage())
                    LastError = new ApplicationException("SMU is not responding to test message!");

                Status = IOModule.LibStatus.OK;
            }
            catch (Exception ex)
            {
                LastError = ex;
                Status = IOModule.LibStatus.PARTIALLY_OK;
            }
        }

        // [31-28] ccd index
        // [27-24] ccx index (always 0 for Zen3 where each ccd has just one ccx)
        // [23-20] core index
        public uint MakeCoreMask(uint core = 0, uint ccd = 0, uint ccx = 0)
        {
            uint ccxInCcd = info.family == Family.FAMILY_19H ? 1U : 2U;
            uint coresInCcx = 8 / ccxInCcd;

            return ((ccd << 4 | ccx % ccxInCcd & 0xF) << 4 | core % coresInCcx & 0xF) << 20;
        }

        public bool ReadDwordEx(uint addr, ref uint data)
        {
            bool res = false;
            if (Ring0.WaitPciBusMutex(10))
            {
                if (Ring0.WritePciConfig(smu.SMU_PCI_ADDR, smu.SMU_OFFSET_ADDR, addr))
                    res = Ring0.ReadPciConfig(smu.SMU_PCI_ADDR, smu.SMU_OFFSET_DATA, out data);
                Ring0.ReleasePciBusMutex();
            }
            return res;
        }

        public uint ReadDword(uint addr)
        {
            uint data = 0;

            if (Ring0.WaitPciBusMutex(10))
            {
                Ring0.WritePciConfig(smu.SMU_PCI_ADDR, (byte)smu.SMU_OFFSET_ADDR, addr);
                Ring0.ReadPciConfig(smu.SMU_PCI_ADDR, (byte)smu.SMU_OFFSET_DATA, out data);
                Ring0.ReleasePciBusMutex();
            }

            return data;
        }

        public bool WriteDwordEx(uint addr, uint data)
        {
            bool res = false;
            if (Ring0.WaitPciBusMutex(10))
            {
                if (Ring0.WritePciConfig(smu.SMU_PCI_ADDR, (byte)smu.SMU_OFFSET_ADDR, addr))
                    res = Ring0.WritePciConfig(smu.SMU_PCI_ADDR, (byte)smu.SMU_OFFSET_DATA, data);
                Ring0.ReleasePciBusMutex();
            }

            return res;
        }

        public double GetCoreMulti(int index = 0)
        {
            if (!Ring0.RdmsrTx(0xC0010293, out uint eax, out uint edx, GroupAffinity.Single(0, index)))
                return 0;

            double multi = 25 * (eax & 0xFF) / (12.5 * (eax >> 8 & 0x3F));
            return Math.Round(multi * 4, MidpointRounding.ToEven) / 4;
        }

        public bool Cpuid(uint index, ref uint eax, ref uint ebx, ref uint ecx, ref uint edx)
        {
            return Opcode.Cpuid(index, 0, out eax, out ebx, out ecx, out edx);
        }

        public bool ReadMsr(uint index, ref uint eax, ref uint edx)
        {
            return Ring0.Rdmsr(index, out eax, out edx);
        }

        public bool ReadMsrTx(uint index, ref uint eax, ref uint edx)
        {
            GroupAffinity affinity = GroupAffinity.Single(0, (int)index);

            return Ring0.RdmsrTx(index, out eax, out edx, affinity);
        }

        public bool WriteMsr(uint msr, uint eax, uint edx)
        {
            bool res = true;

            for (var i = 0; i < info.logicalCores; i++)
            {
                res = Ring0.WrmsrTx(msr, eax, edx, GroupAffinity.Single(0, i));
            }

            return res;
        }

        public void WriteIoPort(uint port, byte value) => Ring0.WriteIoPort(port, value);
        public bool ReadPciConfig(uint pciAddress, uint regAddress, ref uint value) => Ring0.ReadPciConfig(pciAddress, regAddress, out value);
        public uint GetPciAddress(byte bus, byte device, byte function) => Ring0.GetPciAddress(bus, device, function);

        // https://en.wikichip.org/wiki/amd/cpuid
        public CodeName GetCodeName(CPUInfo cpuInfo)
        {
            CodeName codeName = CodeName.Unsupported;

            if (cpuInfo.family == Family.FAMILY_15H)
            {
                switch (cpuInfo.model)
                {
                    case 0x65:
                        codeName = CodeName.BristolRidge;
                        break;
                }
            }
            else if (cpuInfo.family == Family.FAMILY_17H)
            {
                switch (cpuInfo.model)
                {
                    // Zen
                    case 0x1:
                        if (cpuInfo.packageType == PackageType.SP3)
                            codeName = CodeName.Naples;
                        else if (cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.Whitehaven;
                        else
                            codeName = CodeName.SummitRidge;
                        break;
                    case 0x11:
                        codeName = CodeName.RavenRidge;
                        break;
                    case 0x20:
                        codeName = CodeName.Dali;
                        break;
                    // Zen+
                    case 0x8:
                        if (cpuInfo.packageType == PackageType.SP3 || cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.Colfax;
                        else
                            codeName = CodeName.PinnacleRidge;
                        break;
                    case 0x18:
                        codeName = CodeName.Picasso;
                        break;
                    case 0x50: // Subor Z+, CPUID 0x00850F00
                        codeName = CodeName.FireFlight;
                        break;
                    // Zen2
                    case 0x31:
                        if (cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.CastlePeak;
                        else
                            codeName = CodeName.Rome;
                        break;
                    case 0x60:
                        codeName = CodeName.Renoir;
                        break;
                    case 0x68:
                        codeName = CodeName.Lucienne;
                        break;
                    case 0x71:
                        codeName = CodeName.Matisse;
                        break;
                    case 0x90:
                        codeName = CodeName.VanGogh;
                        break;

                    default:
                        codeName = CodeName.Unsupported;
                        break;
                }
            }
            else if (cpuInfo.family == Family.FAMILY_19H)
            {
                switch (cpuInfo.model)
                {
                    // Does Chagall (Zen3 TR) has different model number than Milan?
                    case 0x1:
                        if (cpuInfo.packageType == PackageType.TRX)
                            codeName = CodeName.Chagall;
                        else
                            codeName = CodeName.Milan;
                        break;
                    case 0x21:
                        codeName = CodeName.Vermeer;
                        break;
                    case 0x40:
                        codeName = CodeName.Rembrandt;
                        break;
                    case 0x50:
                        codeName = CodeName.Cezanne;
                        break;

                    default:
                        codeName = CodeName.Unsupported;
                        break;
                }
            }

            return codeName;
        }

        // SVI2 interface
        public SVI2 GetSVI2Info(CodeName codeName)
        {
            var svi = new SVI2();

            switch (codeName)
            {
                case CodeName.BristolRidge:
                    break;

                //Zen, Zen+
                case CodeName.SummitRidge:
                case CodeName.PinnacleRidge:
                case CodeName.RavenRidge:
                case CodeName.FireFlight:
                case CodeName.Dali:
                    svi.coreAddress = F17H_M01H_SVI_TEL_PLANE0;
                    svi.socAddress = F17H_M01H_SVI_TEL_PLANE1;
                    break;

                // Zen Threadripper/EPYC
                case CodeName.Whitehaven:
                case CodeName.Naples:
                case CodeName.Colfax:
                    svi.coreAddress = F17H_M01H_SVI_TEL_PLANE1;
                    svi.socAddress = F17H_M01H_SVI_TEL_PLANE0;
                    break;

                // Zen2 Threadripper/EPYC
                case CodeName.CastlePeak:
                case CodeName.Rome:
                    svi.coreAddress = F17H_M30H_SVI_TEL_PLANE0;
                    svi.socAddress = F17H_M30H_SVI_TEL_PLANE1;
                    break;

                // Picasso
                case CodeName.Picasso:
                    if ((smu.Version & 0xFF000000) > 0)
                    {
                        svi.coreAddress = F17H_M01H_SVI_TEL_PLANE0;
                        svi.socAddress = F17H_M01H_SVI_TEL_PLANE1;
                    }
                    else
                    {
                        svi.coreAddress = F17H_M01H_SVI_TEL_PLANE1;
                        svi.socAddress = F17H_M01H_SVI_TEL_PLANE0;
                    }
                    break;

                // Zen2
                case CodeName.Matisse:
                    svi.coreAddress = F17H_M70H_SVI_TEL_PLANE0;
                    svi.socAddress = F17H_M70H_SVI_TEL_PLANE1;
                    break;

                // Zen2 APU, Zen3 APU ?
                case CodeName.Renoir:
                case CodeName.Lucienne:
                case CodeName.Cezanne:
                case CodeName.VanGogh:
                case CodeName.Rembrandt:
                    svi.coreAddress = F17H_M60H_SVI_TEL_PLANE0;
                    svi.socAddress = F17H_M60H_SVI_TEL_PLANE1;
                    break;

                // Zen3, Zen3 Threadripper/EPYC ?
                case CodeName.Vermeer:
                case CodeName.Chagall:
                case CodeName.Milan:
                    svi.coreAddress = F19H_M21H_SVI_TEL_PLANE0;
                    svi.socAddress = F19H_M21H_SVI_TEL_PLANE1;
                    break;

                default:
                    svi.coreAddress = F17H_M01H_SVI_TEL_PLANE0;
                    svi.socAddress = F17H_M01H_SVI_TEL_PLANE1;
                    break;
            }

            return svi;
        }

        public string GetCpuName()
        {
            string model = "";

            if (Opcode.Cpuid(0x80000002, 0, out uint eax, out uint ebx, out uint ecx, out uint edx))
                model = model + Utils.IntToStr(eax) + Utils.IntToStr(ebx) + Utils.IntToStr(ecx) + Utils.IntToStr(edx);

            if (Opcode.Cpuid(0x80000003, 0, out eax, out ebx, out ecx, out edx))
                model = model + Utils.IntToStr(eax) + Utils.IntToStr(ebx) + Utils.IntToStr(ecx) + Utils.IntToStr(edx);

            if (Opcode.Cpuid(0x80000004, 0, out eax, out ebx, out ecx, out edx))
                model = model + Utils.IntToStr(eax) + Utils.IntToStr(ebx) + Utils.IntToStr(ecx) + Utils.IntToStr(edx);

            return model.Trim();
        }
        public uint GetPatchLevel()
        {
            if (Ring0.Rdmsr(0x8b, out uint eax, out uint edx))
                return eax;

            return 0;
        }

        public bool GetOcMode()
        {
            if (info.codeName == CodeName.SummitRidge)
            {
                if (Ring0.Rdmsr(0xC0010063, out uint eax, out uint edx))
                {
                    // Summit Ridge, Raven Ridge
                    return Convert.ToBoolean((eax >> 1) & 1);
                }
                return false;
            }

            if (info.family == Family.FAMILY_15H)
            {
                return false;
            }

            return Equals(GetPBOScalar(), 0.0f);
        }

        public float GetPBOScalar()
        {
            var cmd = new SMUCommands.GetPBOScalar(smu);
            cmd.Execute();

            return cmd.Scalar;
        }

        public bool SendTestMessage() => new SMUCommands.SendTestMessage(smu).Execute().Success;
        public uint GetSmuVersion() => new SMUCommands.GetSmuVersion(smu).Execute().args[0];
        public SMU.Status TransferTableToDram() => new SMUCommands.TransferTableToDram(smu).Execute().status;
        public uint GetTableVersion() => new SMUCommands.GetTableVersion(smu).Execute().args[0];
        public uint GetDramBaseAddress() => new SMUCommands.GetDramAddress(smu).Execute().args[0];
        public SMU.Status SetPPTLimit(uint arg = 0U) => new SMUCommands.SetSmuLimit(smu).Execute(smu.Rsmu.SMU_MSG_SetPPTLimit, arg).status;
        public SMU.Status SetEDCVDDLimit(uint arg = 0U) => new SMUCommands.SetSmuLimit(smu).Execute(smu.Rsmu.SMU_MSG_SetEDCVDDLimit, arg).status;
        public SMU.Status SetEDCSOCLimit(uint arg = 0U) => new SMUCommands.SetSmuLimit(smu).Execute(smu.Rsmu.SMU_MSG_SetEDCSOCLimit, arg).status;
        public SMU.Status SetTDCVDDLimit(uint arg = 0U) => new SMUCommands.SetSmuLimit(smu).Execute(smu.Rsmu.SMU_MSG_SetTDCVDDLimit, arg).status;
        public SMU.Status SetTDCSOCLimit(uint arg = 0U) => new SMUCommands.SetSmuLimit(smu).Execute(smu.Rsmu.SMU_MSG_SetTDCSOCLimit, arg).status;
        public SMU.Status EnableOcMode() => new SMUCommands.SetOcMode(smu).Execute(true).status;
        public SMU.Status DisableOcMode() => new SMUCommands.SetOcMode(smu).Execute(true).status;
        public SMU.Status SetPBOScalar(uint scalar) => new SMUCommands.SetPBOScalar(smu).Execute(scalar).status;

        public SMU.Status RefreshPowerTable()
        {
            if (powerTable != null && powerTable.DramBaseAddress > 0)
            {
                try
                {
                    SMU.Status status = TransferTableToDram();

                    if (status != SMU.Status.OK)
                        return status;

                    float[] table = new float[powerTable.TableSize / 4];

                    if (Utils.Is64Bit)
                    {
                        byte[] bytes = io.ReadMemory(new IntPtr(powerTable.DramBaseAddress), powerTable.TableSize);
                        if (bytes != null && bytes.Length > 0)
                            Buffer.BlockCopy(bytes, 0, table, 0, bytes.Length);
                        else
                            return SMU.Status.FAILED;
                    }
                    else
                    {
                        /*uint data = 0;

                        for (int i = 0; i < table.Length; ++i)
                        {
                            Ring0.ReadMemory((ulong)(powerTable.DramBaseAddress), ref data);
                            byte[] bytes = BitConverter.GetBytes(data);
                            table[i] = BitConverter.ToSingle(bytes, 0);
                            //table[i] = data;
                        }*/

                        for (int i = 0; i < table.Length; ++i)
                        {
                            int offset = i * 4;
                            io.GetPhysLong((UIntPtr)(powerTable.DramBaseAddress + offset), out uint data);
                            byte[] bytes = BitConverter.GetBytes(data);
                            Buffer.BlockCopy(bytes, 0, table, offset, bytes.Length);
                        }
                    }

                    if (Utils.AllZero(table))
                        status = SMU.Status.FAILED;
                    else
                        powerTable.Table = table;

                    return status;
                }
                catch { }
            }
            return SMU.Status.FAILED;
        }
        public int GetPsmMarginSingleCore(uint coreMask)
        {
            var cmd = new SMUCommands.GetPsmMarginSingleCore(smu);
            cmd.Execute(coreMask);
            return cmd.Margin;
        }
        public int GetPsmMarginSingleCore(uint core, uint ccd, uint ccx) => GetPsmMarginSingleCore(MakeCoreMask(core, ccd, ccx));
        public bool SetPsmMarginAllCores(int margin) => new SMUCommands.SetPsmMarginAllCores(smu).Execute(margin).Success;
        public bool SetPsmMarginSingleCore(uint coreMask, int margin) => new SMUCommands.SetPsmMarginSingleCore(smu).Execute(coreMask, margin).Success;
        public bool SetPsmMarginSingleCore(uint core, uint ccd, uint ccx, int margin) => SetPsmMarginSingleCore(MakeCoreMask(core, ccd, ccx), margin);
        public bool SetFrequencyAllCore(uint frequency) => new SMUCommands.SetFrequencyAllCore(smu).Execute(frequency).Success;
        public bool SetFrequencySingleCore(uint coreMask, uint frequency) => new SMUCommands.SetFrequencySingleCore(smu).Execute(coreMask, frequency).Success;
        public bool SetFrequencySingleCore(uint core, uint ccd, uint ccx, uint frequency) => SetFrequencySingleCore(MakeCoreMask(core, ccd, ccx), frequency);
        private bool SetFrequencyMultipleCores(uint mask, uint frequency, int count)
        {
            // ((i.CCD << 4 | i.CCX % 2 & 0xF) << 4 | i.CORE % 4 & 0xF) << 20;
            for (uint i = 0; i < count; i++)
            {
                mask = Utils.SetBits(mask, 20, 2, i);
                if (!SetFrequencySingleCore(mask, frequency))
                    return false;
            }
            return true;
        }
        public bool SetFrequencyCCX(uint mask, uint frequency) => SetFrequencyMultipleCores(mask, frequency, 8/*SI.NumCoresInCCX*/);
        public bool SetFrequencyCCD(uint mask, uint frequency)
        {
            bool ret = true;
            for (uint i = 0; i < systemInfo.CCXCount / systemInfo.CCDCount; i++)
            {
                mask = Utils.SetBits(mask, 24, 1, i);
                ret = SetFrequencyCCX(mask, frequency);
            }

            return ret;
        }
        public bool IsProchotEnabled()
        {
            uint data = ReadDword(0x59804);
            return (data & 1) == 1;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    io.Dispose();
                    Ring0.Close();
                    Opcode.Close();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
