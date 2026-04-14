using System.Collections.Generic;

namespace VehicleControlApp.Models
{
    public class VehicleParameter
    {
        public int Index { get; set; }
        public string VariableName { get; set; }
        public string DataType { get; set; }
        public string CurrentValue { get; set; }
        public int ArraySize { get; set; }
        public int ArrayIndex { get; set; }
        public string DisplayName { get; set; }
    }

    public class VehicleParameterConfig
    {
        public List<VehicleParameter> VehicleParameters { get; set; } = new List<VehicleParameter>();
    }
}