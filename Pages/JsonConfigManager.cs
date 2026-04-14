using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VehicleControlApp.Models;

namespace VehicleControlApp.Services
{
    public static class JsonConfigManager
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        public static bool SaveConfigToFile(List<VehicleParameter> parameters, string filePath)
        {
            try
            {
                var config = new VehicleParameterConfig
                {
                    VehicleParameters = parameters
                };

                string jsonString = JsonSerializer.Serialize(config, _jsonOptions);
                File.WriteAllText(filePath, jsonString);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存配置文件失败: {ex.Message}");
                return false;
            }
        }

        public static List<VehicleParameter> LoadConfigFromFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return new List<VehicleParameter>();

                string jsonString = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<VehicleParameterConfig>(jsonString, _jsonOptions);
                return config?.VehicleParameters ?? new List<VehicleParameter>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载配置文件失败: {ex.Message}");
                return new List<VehicleParameter>();
            }
        }

        public static List<VehicleParameter> GenerateDefaultParameters()
        {
            var parameters = new List<VehicleParameter>();
            int index = 0;

            // 添加单个参数的方法
            void AddParameter(string variableName, string dataType, string chineseDescription)
            {
                // 解析数据类型，检查是否为数组
                bool isArray = dataType.Contains('[');
                int arraySize = 1;
                string baseType = dataType;

                if (isArray)
                {
                    // 提取数组大小
                    int start = dataType.IndexOf('[') + 1;
                    int end = dataType.IndexOf(']');
                    if (int.TryParse(dataType.Substring(start, end - start), out arraySize))
                    {
                        baseType = dataType.Substring(0, dataType.IndexOf('['));
                    }
                }

                if (isArray && arraySize > 1)
                {
                    // 为数组的每个元素创建单独的参数
                    for (int i = 0; i < arraySize; i++)
                    {
                        parameters.Add(new VehicleParameter
                        {
                            Index = index++,
                            VariableName = variableName,
                            DataType = baseType,
                            ArraySize = arraySize,
                            DisplayName = chineseDescription,
                            ArrayIndex = i,
                            CurrentValue = null,
                        });
                    }
                }
                else
                {
                    // 单个参数
                    parameters.Add(new VehicleParameter
                    {
                        Index = index++,
                        VariableName = variableName,
                        DisplayName = chineseDescription,
                        DataType = dataType,
                        CurrentValue = null,
                        ArraySize = 1,
                    });
                }
            }

            // 按照 VehicleParameters_t 结构体的顺序添加参数

            // 空气阻力参数
            AddParameter("VehCan_AirResistanceCoeff", "real32_T", "空气阻力增益");

            // 驾驶模式断点
            AddParameter("VehCan_DriverModeBp", "uint8_T[3]", "整车三速档位断点 - 整车运动模式断点");

            // 限流功能参数
            AddParameter("VehCan_Driver_En", "boolean_T", "限流功能使能");
            AddParameter("VehCan_Driver_EnSetVal", "real32_T", "限流功能设置值");

            // 一键超车参数
            AddParameter("VehCan_DrvA_AccelCnt", "uint16_T", "一键超车计数");
            AddParameter("VehCan_DrvA_AccelFlgEn", "boolean_T", "一键加速FLG设置值使能");
            AddParameter("VehCan_DrvA_AccelFlgSetVal", "boolean_T", "一键加速FLG设置值");
            AddParameter("VehCan_DrvA_AccelSpd", "real32_T", "一键超车转速值");

            // 防盗模式参数
            AddParameter("VehCan_DrvA_AntiTheftAllowSpd", "real32_T", "进入防盗允许转速");

            // 巡航模式参数
            AddParameter("VehCan_DrvA_CruiseCntEn", "boolean_T", "巡航模式计数开关");
            AddParameter("VehCan_DrvA_CruiseFlgEn", "boolean_T", "巡航模式FLG设置值");
            AddParameter("VehCan_DrvA_CruiseFlgSetVal", "boolean_T", "巡航模式FLG设置值使能");

            // 驾驶状态选择
            AddParameter("VehCan_DrvA_DriveMode", "uint8_T", "整车驾驶状态选择");

            // 电子刹车参数
            AddParameter("VehCan_DrvA_ElecBrkCnt", "uint16_T", "电子刹车计数");
            AddParameter("VehCan_DrvA_ElecBrkFlgEn", "boolean_T", "电子刹车模式开关设置值开关");
            AddParameter("VehCan_DrvA_ElecBrkFlgSetVal", "boolean_T", "电子刹车模式开关设置值");

            // Park模式参数
            AddParameter("VehCan_DrvA_ParkAllowSpd", "real32_T", "进入Park模式允许的转速");
            AddParameter("VehCan_DrvA_ParkModeFlgEn", "boolean_T", "P档模式FLG设置值使能");
            AddParameter("VehCan_DrvA_ParkModeFlgSetVal", "boolean_T", "P档模式FLG设置值");
            AddParameter("VehCan_DrvA_ParkToFwdSpd", "real32_T" , "P档退出转速值");

            // 推车助力参数
            AddParameter("VehCan_DrvA_PushAssFlgEn", "boolean_T", "推车助力FLG设置值使能");
            AddParameter("VehCan_DrvA_PushAssFlgSetVal", "boolean_T", "推车助力FLG设置值");
            AddParameter("VehCan_DrvA_PushAssSafeAllowSpd", "real32_T", "推车助力安全转速阈值");
            AddParameter("VehCan_DrvA_PushAssTrq", "real32_T", "推车助力扭矩值");

            // 修复模式参数
            AddParameter("VehCan_DrvA_RepairFlgEn", "boolean_T", "修复模式FLG设置值使能");
            AddParameter("VehCan_DrvA_RepairFlgSetVal", "boolean_T", "修复模式FLG设置值");
            AddParameter("VehCan_DrvA_RepairSpdLim", "real32_T", "修复模式转速限制值");
            AddParameter("VehCan_DrvA_RepairSpdRamp", "real32_T", "修复模式转速斜率");

            // 倒车模式参数
            AddParameter("VehCan_DrvA_ReverseFlgEn", "boolean_T", "倒车FLG设置值使能");
            AddParameter("VehCan_DrvA_ReverseFlgSetVal", "boolean_T", "倒车FLG设置值");
            AddParameter("VehCan_DrvA_ReverseMaxSpd", "real32_T", "倒车模式最大转速");
            AddParameter("VehCan_DrvA_ReverseTrqCoeff", "real32_T", "倒车模式扭矩系数");

            // 转速模式参数
            AddParameter("VehCan_DrvA_SigVehTrqMax", "real32_T", "转速模式扭矩限制值");

            // 扭矩斜率参数
            AddParameter("VehCan_DrvA_SubTrqRamp", "real32_T", "降扭斜率");

            // 巡航模式参数
            AddParameter("VehCan_DrvA_ToCruiseCnt", "uint16_T", "巡航模式计数");
            AddParameter("VehCan_DrvA_ToCruiseSpdErr", "real32_T", "巡航模式转速容差");

            // Park模式参数
            AddParameter("VehCan_DrvA_ToParkWaitCnt", "uint32_T", "Park模式计数");

            // 推车助力参数
            AddParameter("VehCan_DrvA_ToPushAssSpd", "real32_T", "推车助力进入转速值");

            // 巡航模式参数
            AddParameter("VehCan_Drva_ToCruiseSpd", "real32_T", "巡航模式进入转速值");

            // 前进扭矩斜率查表
            AddParameter("VehCan_ForwardTrqRampMapdata", "real32_T[9]", "整车三速档位断点 - 整车运动模式断点 - 前进扭矩斜率查表值");

            // 母线电流传感器参数
            AddParameter("VehCan_IdcAdOffset", "real32_T", "母线电流传感器零漂");
            AddParameter("VehCan_IdcFilterPara", "real32_T", "母线电流传感器滤波参数");
            AddParameter("VehCan_IdcGain", "real32_T", "母线电流传感器增益");

            // IdcKiMapData数组 (24个元素)
            AddParameter("VehCan_IdcKiMapData", "real32_T[24]", "母线电流限制功能Ki查表转速断点 - 母线电流限制功能Ki查表曲线");

            // IdcKpKiMapBp数组 (24个元素)
            AddParameter("VehCan_IdcKpKiMapBp", "real32_T[24]", "母线电流限制功能Ki查表转速断点");

            // IdcKpMapData数组 (24个元素)
            AddParameter("VehCan_IdcKpMapData", "real32_T[24]", "母线电流限制功能Ki查表转速断点 - 母线电流限制功能Kp查表曲线");

            // 能量回收参数
            AddParameter("VehCan_KERS_AnalogBrkConsTrqEn", "boolean_T", "能量回收模拟量输出恒扭矩值开关");
            AddParameter("VehCan_KERS_AnalogBrkConsTrqSetVal", "real32_T", "能量回收模拟量输出恒扭矩值");

            // 能量回收扭矩系数查表
            AddParameter("VehCan_KERS_BrakeTrqGain", "real32_T[25]", "能量回收转速断点 - 能量回收扭矩系数查表值");

            // KERS_EcoModeMapDataSlide数组 (25个元素)
            AddParameter("VehCan_KERS_EcoModeMapDataSlide", "real32_T[25]", "能量回收转速断点 - ECO模式滑行扭矩查表");

            // 能量回收PI控制器参数
            AddParameter("VehCan_KERS_IdcKi", "real32_T", "能量回收母线电流限制Ki");
            AddParameter("VehCan_KERS_IdcKp", "real32_T", "能量回收母线电流限制Kp");
            AddParameter("VehCan_KERS_IdcLim", "real32_T", "能量回收母线电流回馈限制");

            // Normal模式滑行扭矩查表
            AddParameter("VehCan_KERS_NormalModeMapDataSlide", "real32_T[25]", "能量回收转速断点 - Normal模式滑行扭矩查表");

            // 能量回收转速断点
            AddParameter("VehCan_KERS_SpdBp", "real32_T[25]", "能量回收转速断点");

            // 能量回收母线电压限制参数
            AddParameter("VehCan_KERS_UdcKi", "real32_T", "能量回收母线电压限制Ki");
            AddParameter("VehCan_KERS_UdcKp", "real32_T", "能量回收母线电压限制Kp");
            AddParameter("VehCan_KERS_UdcLim", "real32_T", "能量回收母线电压回馈限制");

            // 机械参数
            AddParameter("VehCan_MtrReductionRatio", "real32_T", "电机减速器速比");

            // P档模式参数
            AddParameter("VehCan_ParkSigEn", "boolean_T", "P档模式开关");
            AddParameter("VehCan_ParkSigSetVal", "boolean_T", "P档模式FLG设置值使能");

            // 刹车信号处理参数
            AddParameter("VehCan_SigOpt_AnaBrkValid", "boolean_T", "刹车信号来源开关");
            AddParameter("VehCan_SigOpt_BrkFilterPara", "real32_T", "模拟量刹车滤波系数");
            AddParameter("VehCan_SigOpt_BrkGain", "real32_T", "模拟量刹车系数");
            AddParameter("VehCan_SigOpt_BrkOCV", "real32_T", "模拟量刹车开路电压");
            AddParameter("VehCan_SigOpt_BrkSCV", "real32_T", "模拟量刹车短路电压阈值");
            AddParameter("VehCan_SigOpt_BrkSigEn", "boolean_T", "刹车信号设置值使能");
            AddParameter("VehCan_SigOpt_BrkSigSetVal", "boolean_T", "刹车信号设置值");
            AddParameter("VehCan_SigOpt_BrkVmax", "real32_T", "模拟量刹车有效电平上限");
            AddParameter("VehCan_SigOpt_BrkVmin", "real32_T", "模拟量刹车有效电平下限");

            // 油门信号处理参数
            AddParameter("VehCan_SigOpt_ThrFilterPara", "real32_T", "油门低通滤波参数");
            AddParameter("VehCan_SigOpt_ThrGain", "real32_T", "转把Ad转换系数");
            AddParameter("VehCan_SigOpt_ThrOCV", "real32_T", "油门开路电压阈值");
            AddParameter("VehCan_SigOpt_ThrOpnEn", "boolean_T", "油门开度设置值使能");
            AddParameter("VehCan_SigOpt_ThrOpnSetVal", "real32_T", "油门开度设置值");
            AddParameter("VehCan_SigOpt_ThrSCV", "real32_T", "油门短路电压阈值");
            AddParameter("VehCan_SigOpt_ThrVmax", "real32_T", "油门有效电平上限");
            AddParameter("VehCan_SigOpt_ThrVmin", "real32_T", "油门有效电压下限");

            // 驻坡模式参数
            AddParameter("VehCan_SigVehHillHoldEn", "boolean_T", "驻坡模式开关");

            // 最高转速限制查表
            AddParameter("VehCan_SpdMaxMapData", "real32_T[9]", "整车三速档位断点 - 整车运动模式断点 - 最高转速限制查表值");

            // 油门响应系数查表
            AddParameter("VehCan_ThrRespMapDataEco", "real32_T[11]", "油门响应系数断点 - 油门响应系数ECO模式查表值");
            AddParameter("VehCan_ThrRespMapDataNormal", "real32_T[11]", "油门响应系数断点 - 油门响应系数Normal模式查表值");
            AddParameter("VehCan_ThrRespMapDataSport", "real32_T[11]", "油门响应系数断点 - 油门响应系数Sport模式查表值");

            // 油门响应系数断点
            AddParameter("VehCan_ThrRespPctBp", "real32_T[11]", "油门响应系数断点");

            // 三速档位参数
            AddParameter("VehCan_ThreeGearBp", "uint8_T[3]", "整车三速档位断点");
            AddParameter("VehCan_ThreeGearMode", "boolean_T", "三速模式选择");

            // 扭矩限制系数查表
            AddParameter("VehCan_TrqMaxMapData", "real32_T[9]", "整车三速档位断点 - 整车运动模式断点 - 扭矩限制系数查表值");

            // 整车模块周期
            AddParameter("VehCan_Ts", "real32_T", "整车模块周期");

            // 母线电压滤波参数
            AddParameter("VehCan_UdcFilterPara", "real32_T", "母线电压滤波参数");

            // 母线电压映射参数
            AddParameter("VehCan_UdcMapBp", "real32_T[24]", "限母线电流断点电压");
            AddParameter("VehCan_UdcMapData", "real32_T[24]", "限母线电流断点电压 - 限母线电流曲线");

            // 整车控制模式
            AddParameter("VehCan_VehCtrlMode", "uint8_T", "整车控制模式选择");

            // 机械刹车及电制动参数
            AddParameter("VehCan_VehDigitalBrakeTrqGain", "real32_T", "机械刹车及电制动扭矩转换增益");

            // 摩擦力、重力分力参数
            AddParameter("VehCan_VehExternalForceInitVal", "real32_T", "摩擦力，重力分力初始值");
            AddParameter("VehCan_VehExternalForceInitValCalSpd", "real32_T", "摩擦力，重力分力初始值计算起始转速");
            AddParameter("VehCan_VehMassForceFilterPara", "real32_T", "整车质量估算及摩擦力重力分力估算");
            AddParameter("VehCan_VehMaxMass", "real32_T", "载人整车最大质量");
            AddParameter("VehCan_VehMinMass", "real32_T", "载人整车最小质量");

            // 轮胎半径
            AddParameter("VehCan_VehWheelRadius", "real32_T", "轮胎半径");

            return parameters;
        }
    }
}