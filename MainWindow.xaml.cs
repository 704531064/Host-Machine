using FirstFloor.ModernUI.Windows.Controls;
using LiveCharts;
using LiveCharts.Wpf;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VehicleControlApp.Models;
using VehicleControlApp.Services;

namespace ModbusDataReceiver
{
    public partial class MainWindow : Window
    {
        public static SerialPort serialPort;
        private DispatcherTimer uiTimer;
        private System.Timers.Timer dataTimer;
        private DispatcherTimer sendTimer;
        private BswIf_AppModbusToBswStr_Struct receivedData;
        private int receivedframeCount = 0;
        private int sendFrameCount = 0;
        private DateTime lastUpdateTime;
        private List<byte> receiveBuffer = new List<byte>();
        private bool isSendingData = false; // 定时发送状态
        private bool _isSending = false;    // 发送操作锁
        private readonly object _lockObject = new object();
        private List<VehicleParameter> _parameters;
        private Dictionary<string, List<VehicleParameter>> _parameterGroups;
        private List<ArrayGroup> _arrayGroups;

        private bool _isChartRecording = false;
        private ChartValues<double> _chartValues;
        private List<DateTime> _chartTimeStamps;
        private int _maxDataPoints = 150;
        private string _selectedParameter = "";
        private Dictionary<string, Func<double>> _parameterGetters;
        private byte receiveValue;

        private VehicleParameters_t _vehicleParameters;
        private bool _waitingForParamData = false;

        private const int READ_REQUEST_CMD = 800;         // 实时数据读取地址
        private const int READ_CAL_CMD = 1000;  // 参数读取请求地址

        public static string PathText;

        private bool _isFirmwareUpgrading = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeSerialPort();
            InitializeTimers();
            InitializeEmptyParameters();
            InitializeChart();
            PopulateComPorts();
            receivedData = new BswIf_AppModbusToBswStr_Struct();
        }

        private void InitializeSerialPort()
        {
            serialPort = new SerialPort
            {
                BaudRate = 115200,
                DataBits = 8,
                StopBits = StopBits.One,
                Parity = Parity.None,
                Handshake = Handshake.None,
                ReadTimeout = 2000,
                WriteTimeout = 2000,
                RtsEnable = false,
                ReadBufferSize = 4096,
                WriteBufferSize = 4096
            };

            serialPort.DataReceived += SerialPort_DataReceived;
            serialPort.ErrorReceived += SerialPort_ErrorReceived;
        }

        // 初始化图表
        private void InitializeChart()
        {
            _chartValues = new ChartValues<double>();
            _chartTimeStamps = new List<DateTime>();

            // 初始化参数获取器
            InitializeParameterGetters();

            // 填充参数选择下拉框
            var parameterNames = new List<string>
            {
                "母线电压", "输出电压", "电机转速", "扭矩命令", "扭矩估算值",
                "U相电流", "V相电流", "W相电流", "D轴电流", "Q轴电流",
                "电机温度1", "电机温度2", "MOS温度", "油门开度", "刹车开度",
                "驱动扭矩", "回收扭矩", "速度命令", "ISR负载率", "总负载率"
            };

            ChartParameterComboBox.ItemsSource = parameterNames;
            if (parameterNames.Count > 0)
                ChartParameterComboBox.SelectedIndex = 0;

            // 设置图表属性
            LiveChart.DisableAnimations = true;
            LiveChart.Hoverable = false;
            LiveChart.DataTooltip = null;

            // 初始化图表数据
            ChartSeries.Values = _chartValues;

            // 设置坐标轴格式化
            XAxis.LabelFormatter = value => value.ToString("F0");
            YAxis.LabelFormatter = value => value.ToString("F2");
        }

        // 初始化参数获取器
        private void InitializeParameterGetters()
        {
            _parameterGetters = new Dictionary<string, Func<double>>
            {
                { "母线电压", () => (double)receivedData.UdcAct },
                { "输出电压", () => (double)receivedData.UsOut },
                { "电机转速", () => (double)receivedData.MtrSpd },
                { "扭矩命令", () => (double)receivedData.TrqCmd },
                { "扭矩估算值", () => (double)receivedData.TrqEst },
                { "U相电流", () => (double)receivedData.IuAct },
                { "V相电流", () => (double)receivedData.IvAct },
                { "W相电流", () => (double)receivedData.IwAct },
                { "D轴电流", () => (double)receivedData.IdFb },
                { "Q轴电流", () => (double)receivedData.IqFb },
                { "电机温度1", () => (double)receivedData.MtrT1 },
                { "电机温度2", () => (double)receivedData.MtrT2 },
                { "MOS温度", () => (double)receivedData.PwrMdlUT },
                { "油门开度", () => (double)receivedData.ThrottleOpening },
                { "刹车开度", () => (double)receivedData.BrakeOpening },
                { "驱动扭矩", () => (double)receivedData.DriveTrq },
                { "回收扭矩", () => (double)receivedData.KERS_Trq },
                { "速度命令", () => (double)receivedData.SpdCmd },
                { "ISR负载率", () => (double)receivedData.IsrLoadRatio },
                { "总负载率", () => (double)receivedData.AllLoadRatio }
            };
        }

        // 在UiTimer_Tick方法中添加图表更新
        private void UiTimer_Tick(object sender, EventArgs e)
        {
            UpdateDataDisplay();

            // 如果正在记录图表数据，则更新图表
            if (_isChartRecording && !string.IsNullOrEmpty(_selectedParameter))
            {
                UpdateChartData();
            }
        }

        // 更新图表数据
        private void UpdateChartData()
        {
            try
            {
                if (_parameterGetters.ContainsKey(_selectedParameter))
                {
                    double value = _parameterGetters[_selectedParameter]();
                    DateTime currentTime = DateTime.Now;

                    _chartValues.Add(value);
                    _chartTimeStamps.Add(currentTime);

                    if (_chartValues.Count > _maxDataPoints)
                    {
                        _chartValues.RemoveAt(0);
                        _chartTimeStamps.RemoveAt(0);
                    }

                    YAxis.Title = $"{_selectedParameter} ({GetParameterUnit(_selectedParameter)})";

                    ChartStatusText.Text = $"{value:F2}{GetParameterUnit(_selectedParameter)}";

                    AdjustYAxisRange();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"图表更新错误: {ex.Message}");
            }
        }

        // 动态调整Y轴范围
        private void AdjustYAxisRange()
        {
            if (_chartValues.Count == 0) return;

            double min = _chartValues.Min();
            double max = _chartValues.Max();
            double range = max - min;

            // 添加10%的边距
            double margin = range * 0.1;
            if (margin == 0) margin = Math.Abs(min) * 0.1 + 0.1; // 防止除零

            YAxis.MinValue = min - margin;
            YAxis.MaxValue = max + margin;
        }

        private string GetParameterUnit(string parameterName)
        {
            var units = new Dictionary<string, string>
            {
                { "母线电压", "V" }, { "输出电压", "V" }, { "电机转速", "rpm" },
                { "扭矩命令", "Nm" }, { "扭矩估算值", "Nm" }, { "U相电流", "A" },
                { "V相电流", "A" }, { "W相电流", "A" }, { "D轴电流", "A" },
                { "Q轴电流", "A" }, { "电机温度1", "°C" }, { "电机温度2", "°C" },
                { "MOS温度", "°C" }, { "油门开度", "%" }, { "刹车开度", "%" },
                { "驱动扭矩", "Nm" }, { "回收扭矩", "Nm" }, { "速度命令", "rpm" },
                { "ISR负载率", "%" }, { "总负载率", "%" }
            };

            return units.ContainsKey(parameterName) ? units[parameterName] : "";
        }

        private void StartChartRecording()
        {
            if (ChartParameterComboBox.SelectedItem == null)
            {
                _selectedParameter = "母线电压"; // 默认参数
                ChartParameterComboBox.SelectedItem = _selectedParameter;
            }
            else
            {
                _selectedParameter = ChartParameterComboBox.SelectedItem.ToString();
            }

            _isChartRecording = true;

            // 根据选择的时间范围设置最大数据点数
            if (TimeRangeComboBox.SelectedItem != null)
            {
                string timeRangeStr = (TimeRangeComboBox.SelectedItem as ComboBoxItem).Content.ToString();
                int timeRange = int.Parse(timeRangeStr.Replace("秒", ""));
                _maxDataPoints = timeRange * 5; // 5Hz采样率
            }

            // 清除旧数据
            _chartValues.Clear();
            _chartTimeStamps.Clear();

            // 重置Y轴范围
            YAxis.MinValue = double.NaN;
            YAxis.MaxValue = double.NaN;

            ChartStatusText.Text = $"记录中: {_selectedParameter}";
        }

        // 停止图表记录
        private void StopChartRecording()
        {
            _isChartRecording = false;
            ChartStatusText.Text = "未记录";
        }

        private void ClearChartButton_Click(object sender, RoutedEventArgs e)
        {
            _chartValues.Clear();
            _chartTimeStamps.Clear();
            ChartStatusText.Text = "数据已清除";

            // 重置Y轴范围
            YAxis.MinValue = double.NaN;
            YAxis.MaxValue = double.NaN;
        }

        private void TimeRangeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TimeRangeComboBox.SelectedItem != null)
            {
                string timeRangeStr = (TimeRangeComboBox.SelectedItem as ComboBoxItem).Content.ToString();
                int timeRange = int.Parse(timeRangeStr.Replace("秒", ""));
                _maxDataPoints = timeRange * 5; // 5Hz采样率

                // 如果正在记录，重新设置数据点数限制
                if (_isChartRecording && _chartValues.Count > _maxDataPoints)
                {
                    // 移除超出限制的数据点
                    while (_chartValues.Count > _maxDataPoints)
                    {
                        _chartValues.RemoveAt(0);
                        _chartTimeStamps.RemoveAt(0);
                    }
                }
            }
        }

        private void ChartParameterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChartParameterComboBox.SelectedItem != null)
            {
                string newParameter = ChartParameterComboBox.SelectedItem.ToString();

                if (_isChartRecording)
                {
                    SwitchRecordingParameter(newParameter);
                }
                else
                {
                    _selectedParameter = newParameter;
                    ChartStatusText.Text = $"准备记录: {_selectedParameter}";
                }
            }
        }

        private void SwitchRecordingParameter(string newParameter)
        {
            if (string.IsNullOrEmpty(newParameter) || newParameter == _selectedParameter)
                return;

            try
            {
                _chartValues.Clear();
                _chartTimeStamps.Clear();

                _selectedParameter = newParameter;

                YAxis.MinValue = double.NaN;
                YAxis.MaxValue = double.NaN;

                ChartStatusText.Text = $"记录中: {_selectedParameter}";

                if (_parameterGetters.ContainsKey(_selectedParameter))
                {
                    try
                    {
                        double value = _parameterGetters[_selectedParameter]();
                        ChartStatusText.Text = $"{value:F2}{GetParameterUnit(_selectedParameter)}";
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"切换观测参数错误: {ex.Message}");
                ChartStatusText.Text = $"切换失败: {_selectedParameter}";
            }
        }

        // 在窗口关闭时清理资源
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (serialPort.IsOpen)
                serialPort.Close();
            uiTimer.Stop();
            dataTimer.Stop();
            sendTimer.Stop();

            // 清理图表资源
            _chartValues?.Clear();
            _chartTimeStamps?.Clear();
        }

        private void InitializeTimers()
        {
            // UI更新定时器
            uiTimer = new DispatcherTimer();
            uiTimer.Interval = TimeSpan.FromMilliseconds(100);
            uiTimer.Tick += UiTimer_Tick;

            // 数据接收超时定时器
            dataTimer = new System.Timers.Timer(500);
            dataTimer.Elapsed += DataTimer_Elapsed;
            dataTimer.AutoReset = false;

            // 发送数据定时器
            sendTimer = new DispatcherTimer();
            sendTimer.Interval = TimeSpan.FromMilliseconds(200);
            sendTimer.Tick += SendTimer_Tick;
        }

        private void PopulateComPorts()
        {
            string[] ports = SerialPort.GetPortNames();
            ComPortComboBox.ItemsSource = ports;
            if (ports.Length > 0)
            {
                ComPortComboBox.SelectedIndex = 0;
            }
        }

        private void CreateNonArrayTabPage()
        {
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(1)
            };

            var wrapPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(1)
            };

            var groupStyles = new List<(Brush Background, Brush Border)>
            {
                (new SolidColorBrush(Color.FromRgb(250, 253, 255)), new SolidColorBrush(Color.FromRgb(186, 220, 250))), // 浅蓝
                (new SolidColorBrush(Color.FromRgb(249, 253, 244)), new SolidColorBrush(Color.FromRgb(193, 221, 180))), // 浅绿
                (new SolidColorBrush(Color.FromRgb(255, 252, 245)), new SolidColorBrush(Color.FromRgb(240, 203, 163))), // 浅橙
                (new SolidColorBrush(Color.FromRgb(255, 248, 250)), new SolidColorBrush(Color.FromRgb(244, 187, 204))), // 浅粉
                (new SolidColorBrush(Color.FromRgb(248, 248, 255)), new SolidColorBrush(Color.FromRgb(194, 195, 255))), // 浅紫
                (new SolidColorBrush(Color.FromRgb(253, 245, 230)), new SolidColorBrush(Color.FromRgb(233, 196, 106))), // 浅黄
                (new SolidColorBrush(Color.FromRgb(240, 248, 255)), new SolidColorBrush(Color.FromRgb(145, 191, 219))), // 天蓝
                (new SolidColorBrush(Color.FromRgb(250, 240, 230)), new SolidColorBrush(Color.FromRgb(210, 180, 140))), // 浅棕
                (new SolidColorBrush(Color.FromRgb(245, 250, 245)), new SolidColorBrush(Color.FromRgb(152, 187, 152))), // 薄荷绿
                (new SolidColorBrush(Color.FromRgb(255, 245, 245)), new SolidColorBrush(Color.FromRgb(224, 176, 176)))  // 浅红
            };

            int groupIndex = 0;
            foreach (var group in _parameterGroups.OrderBy(g => g.Key))
            {
                var colorSet = groupStyles[groupIndex % groupStyles.Count]; // 循环使用配色

                var groupBorder = new Border
                {
                    BorderBrush = colorSet.Border, // 每个分组不同的边框色
                    BorderThickness = new Thickness(1),
                    Background = colorSet.Background, // 每个分组不同的背景色
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(1),
                    Width = 300, // 每个分组固定宽度，适配参数控件
                    Margin = new Thickness(1)
                };

                var groupStackPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical
                };

                // 添加分组标题
                var groupTitle = new TextBlock
                {
                    Text = group.Key,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = colorSet.Border,
                    Foreground = Brushes.White,
                    Padding = new Thickness(1),
                    Margin = new Thickness(0, 0, 0, 1)
                };
                groupStackPanel.Children.Add(groupTitle);

                foreach (var parameter in group.Value)
                {
                    var paramControl = CreateCompactParameterControl(parameter);
                    groupStackPanel.Children.Add(paramControl);
                }

                groupBorder.Child = groupStackPanel;
                wrapPanel.Children.Add(groupBorder);

                groupIndex++;
            }

            scrollViewer.Content = wrapPanel;

            var nonArrayTabItem = new TabItem
            {
                Header = "非数组参数",
                Content = scrollViewer,
                Style = (Style)FindResource(typeof(TabItem)) ?? new Style(typeof(TabItem))
            };

            MainTabControl.Items.Add(nonArrayTabItem);
        }

        private Border CreateCompactParameterControl(VehicleParameter parameter)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(4),
                Width = 290
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            // 变量名 - 使用DisplayName（中文名称）
            var nameTextBlock = new TextBlock
            {
                Text = parameter.DisplayName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                ToolTip = CreateVariableToolTip(parameter)
            };
            Grid.SetColumn(nameTextBlock, 0);
            grid.Children.Add(nameTextBlock);

            // 值输入框
            var valueTextBox = new TextBox
            {
                Text = parameter.CurrentValue,
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = parameter,
                IsReadOnly = false,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            valueTextBox.TextChanged += (s, e) =>
            {
                parameter.CurrentValue = valueTextBox.Text;
            };

            // 添加键盘Enter键事件处理
            valueTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    AutoSendParameter(parameter);
                }
            };

            Grid.SetColumn(valueTextBox, 1);
            grid.Children.Add(valueTextBox);

            border.Child = grid;
            return border;
        }

        private ToolTip CreateVariableToolTip(VehicleParameter parameter)
        {
            var toolTip = new ToolTip();
            var stackPanel = new StackPanel();

            // 变量名
            stackPanel.Children.Add(new TextBlock
            {
                Text = $"变量名: {parameter.VariableName}",
                FontWeight = FontWeights.Bold,
                FontSize = 12
            });

            // 数据类型
            stackPanel.Children.Add(new TextBlock
            {
                Text = $"数据类型: {parameter.DataType}",
                FontSize = 11
            });

            stackPanel.Children.Add(new TextBlock
            {
                Text = $"Index: {parameter.Index}",
                FontSize = 11
            });

            toolTip.Content = stackPanel;
            return toolTip;
        }

        private void AutoSendParameter(VehicleParameter parameter)
        {
            if (!serialPort.IsOpen)
            {
                return;
            }

            try
            {
                if (isSendingData)
                {
                    StopTimedSending();

                    Thread.Sleep(200);
                }

                SendSingleParameter(parameter.Index, parameter.CurrentValue, 1);

                bool success = WaitForAck();

                if (success)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatusText.Text = $"参数 {parameter.DisplayName} 修改成功";
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText.Text = $"发送失败: {ex.Message}";
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private bool WaitForAck()
        {
            try
            {
                const int timeout = 1000;  // 最大等待时间
                var startTime = Environment.TickCount;
                bool receiveFlag = false;
                while (Environment.TickCount - startTime < timeout)
                {
                    if (receiveValue == 0x06)
                    {
                        receiveValue = 0;
                        receivedframeCount++;
                        Dispatcher.Invoke(() =>
                        {
                            FrameCountTextBox.Text = receivedframeCount.ToString();
                        });
                        receiveFlag = true;
                        return true;
                    }
                    else if (receiveValue == 0x43)
                    {
                        receiveValue = 0;
                        receivedframeCount++;
                        Dispatcher.Invoke(() =>
                        {
                            FrameCountTextBox.Text = receivedframeCount.ToString();
                        });
                        receiveFlag = true;
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show("修改失败", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                        return false;
                    }
                }
                if (receiveFlag == false)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show("发送超时，未收到下位机确认", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void StopTimedSending()
        {
            lock (_lockObject)
            {
                if (isSendingData)
                {
                    sendTimer.Stop();
                    isSendingData = false;

                    Dispatcher.Invoke(() =>
                    {
                        SendDataButton.Content = "开始接收";
                        SendDataButton.Background = new SolidColorBrush(Color.FromRgb(0x0, 0x80, 0x0));

                        // 停止图表记录
                        StopChartRecording();
                    });
                }
            }
        }

        public bool SendSingleParameter(int index, string value, byte rw)
        {
            lock (_lockObject)
            {
                if (_isSending)  // 使用专门的发送锁
                    return false;

                _isSending = true;

                try
                {
                    if (!serialPort.IsOpen)
                        return false;

                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();

                    var frame = BuildSingleParameterFrame(index, value, rw);

                    if (frame.Length != 12)
                        return false;

                    bool sendSuccess = SendDataWithRS485Control(frame);

                    if (sendSuccess)
                    {
                        sendFrameCount++;
                        // 更新UI显示发送帧数
                        Dispatcher.Invoke(() =>
                        {
                            SendFrameCountTextBox.Text = sendFrameCount.ToString();
                        });
                    }

                    return sendSuccess;
                }
                catch (Exception)
                {
                    return false;
                }
                finally
                {
                    _isSending = false;  // 确保锁被释放
                }
            }
        }

        private bool SendDataWithRS485Control(byte[] data)
        {
            try
            {
                Thread.Sleep(2);

                serialPort.BaseStream.Write(data, 0, data.Length);
                serialPort.BaseStream.Flush();

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private byte[] BuildSingleParameterFrame(int index, string value, byte rw)
        {
            var frame = new List<byte>();

            try
            {
                frame.Add(0x5A);
                frame.Add(0xA5);

                frame.Add((byte)(index & 0xFF)); // 低字节
                frame.Add((byte)(index >> 8));   // 高字节

                frame.Add(rw);

                float floatValue;
                if (!float.TryParse(value, out floatValue))
                {
                    floatValue = 0.0f;
                }

                byte[] floatBytes = BitConverter.GetBytes(floatValue);

                frame.AddRange(floatBytes);

                ushort crc = CalculateCrc16(frame.ToArray(), (ushort)frame.Count);

                // CRC使用小端模式
                frame.Add((byte)(crc & 0xFF));  // CRC低字节
                frame.Add((byte)(crc >> 8));    // CRC高字节

                frame.Add(0x3C);

                return frame.ToArray();
            }
            catch (Exception)
            {
                return new byte[0];
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (!serialPort.IsOpen) return;

                if (_isFirmwareUpgrading) return;

                int bytesToRead = serialPort.BytesToRead;
                if (bytesToRead > 0)
                {
                    byte[] buffer = new byte[bytesToRead];
                    int bytesRead = serialPort.Read(buffer, 0, bytesToRead);

                    if (bytesRead > 0)
                    {
                        receiveBuffer.AddRange(buffer);

                        dataTimer.Stop();
                        dataTimer.Start();

                        Dispatcher.Invoke(() => ProcessReceiveBuffer());
                    }
                }
            }
            catch (Exception)
            {

            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBox.Text = $"串口错误: {e.EventType}";
                StatusText.Text = $"串口错误: {e.EventType}";
            });
        }

        private void ProcessReceiveBuffer()
        {
            while (receiveBuffer.Count >= 8)  // 最小帧长度现在是8字节（2字节头+2字节长度+1字节数据+2字节CRC+1字节尾）
            {
                int frameStart = -1;
                for (int i = 0; i <= receiveBuffer.Count - 2; i++)
                {
                    if (receiveBuffer[i] == 0x5A && receiveBuffer[i + 1] == 0xA5)
                    {
                        frameStart = i;
                        break;
                    }
                }

                if (frameStart == -1)
                {
                    receiveBuffer.Clear();
                    return;
                }

                if (frameStart > 0)
                {
                    receiveBuffer.RemoveRange(0, frameStart);
                }

                if (receiveBuffer.Count < 4)  // 至少有帧头+长度字段
                {
                    return;
                }

                // 解析数据长度（2字节，低字节在前）
                int dataLength = receiveBuffer[2] | (receiveBuffer[3] << 8);
                int totalFrameLength = dataLength + 7;  // 2字节头+2字节长度+数据+2字节CRC+1字节尾

                if (receiveBuffer.Count < totalFrameLength)
                {
                    return;
                }

                byte[] frameData = receiveBuffer.GetRange(0, totalFrameLength).ToArray();
                ProcessReceivedData(frameData);
                receiveBuffer.RemoveRange(0, totalFrameLength);
            }
        }

        private void ProcessReceivedData(byte[] data)
        {
            if (data.Length < 8 || data[0] != 0x5A || data[1] != 0xA5)
            {
                Dispatcher.Invoke(() => StatusText.Text = "无效帧头");
                return;
            }

            // 解析数据长度（2字节）
            int dataLength = data[2] | (data[3] << 8);

            if (data.Length != dataLength + 7)  // 总长度 = 数据长度 + 7字节开销
            {
                Dispatcher.Invoke(() => StatusText.Text = "数据长度不匹配");
                return;
            }

            if (dataLength == 1)
            {
                receiveValue = data[4];  // 现在数据从第4个字节开始
                return;
            }

            byte[] dataPayload = new byte[dataLength];
            Array.Copy(data, 4, dataPayload, 0, dataLength);

            // 检查CRC校验（从帧头到数据部分）
            ushort receivedCrc = (ushort)(data[dataLength + 4] | (data[dataLength + 5] << 8));

            // 计算CRC的数据范围：帧头、长度字段、数据
            ushort calculatedCrc = CalculateCrc16(data, 0, dataLength + 4);

            if (receivedCrc != calculatedCrc)
            {
                Dispatcher.Invoke(() => StatusText.Text = "CRC校验失败");
                return;
            }

            if (data[dataLength + 6] != 0x3C)
            {
                Dispatcher.Invoke(() => StatusText.Text = "帧尾错误");
                return;
            }

            // 判断数据类型
            int realTimeDataSize = Marshal.SizeOf(typeof(BswIf_AppModbusToBswStr_Struct));
            int paramDataSize = Marshal.SizeOf(typeof(VehicleParameters_t));

            if (dataLength == realTimeDataSize)
            {
                // 实时数据
                ParseDataToNewStructure(dataPayload);
                receivedframeCount++;
            }
            else if (dataLength == paramDataSize && _waitingForParamData)
            {
                // 参数数据
                ParseDataToVehicleParameters(dataPayload);
                _waitingForParamData = false;
            }
            else
            {
                Dispatcher.Invoke(() => StatusText.Text = $"未知数据类型，长度: {dataLength}");
                return;
            }

            Dispatcher.Invoke(() =>
            {
                FrameCountTextBox.Text = receivedframeCount.ToString();
                lastUpdateTime = DateTime.Now;
                LastUpdateText.Text = lastUpdateTime.ToString("HH:mm:ss.fff");
            });
        }

        private ushort CalculateCrc16(byte[] data, int startIndex, int length)
        {
            ushort crc = 0xFFFF;

            for (int i = startIndex; i < startIndex + length; i++)
            {
                crc ^= data[i];

                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    }
                    else
                    {
                        crc = (ushort)(crc >> 1);
                    }
                }
            }

            return crc;
        }

        private ushort CalculateCrc16(byte[] data, ushort len)
        {
            return CalculateCrc16(data, 0, len);
        }

        private void DataTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
        }
        private void ParseDataToNewStructure(byte[] data)
        {
            try
            {
                int size = Marshal.SizeOf(typeof(BswIf_AppModbusToBswStr_Struct));

                if (data.Length < size)
                {
                    Dispatcher.Invoke(() => StatusText.Text = "数据长度不足");
                    return;
                }

                IntPtr ptr = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.Copy(data, 0, ptr, size);
                    receivedData = (BswIf_AppModbusToBswStr_Struct)Marshal.PtrToStructure(ptr, typeof(BswIf_AppModbusToBswStr_Struct));
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => StatusText.Text = $"解析错误: {ex.Message}");
            }
        }

        private void SendTimer_Tick(object sender, EventArgs e)
        {
            if (isSendingData)
            {
                SendSingleParameter(READ_REQUEST_CMD, "0", 0);
            }
        }

        private void Button_Click_RefreshCom(object sender, RoutedEventArgs e)
        {
            try
            {
                // 检查串口是否打开
                if (serialPort != null && serialPort.IsOpen)
                {
                    ModernDialog.ShowMessage("请先关闭串口再刷新！", "提示", MessageBoxButton.OK);
                    return;
                }

                // 保存当前选择
                string selectedPort = ComPortComboBox.SelectedItem?.ToString();

                // 重新获取串口列表
                string[] ports = SerialPort.GetPortNames();

                // 排序串口（按COM后面的数字）
                Array.Sort(ports, (x, y) =>
                {
                    try
                    {
                        int xNum = int.Parse(x.Replace("COM", ""));
                        int yNum = int.Parse(y.Replace("COM", ""));
                        return xNum.CompareTo(yNum);
                    }
                    catch
                    {
                        return x.CompareTo(y);
                    }
                });

                // 更新ItemsSource
                ComPortComboBox.ItemsSource = ports;

                // 恢复选择
                if (!string.IsNullOrEmpty(selectedPort) && ports.Contains(selectedPort))
                {
                    ComPortComboBox.SelectedItem = selectedPort;
                }
                else if (ports.Length > 0)
                {
                    ComPortComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                ModernDialog.ShowMessage($"刷新失败: {ex.Message}", "错误", MessageBoxButton.OK);
            }
        }

        private void ReceiveDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (!serialPort.IsOpen)
            {
                MessageBox.Show("请先打开串口连接！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!isSendingData)
            {
                // 开始接收数据
                sendTimer.Start();
                isSendingData = true;
                SendDataButton.Content = "停止接收";
                SendDataButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36));

                // 开始图表记录
                StartChartRecording();
            }
            else
            {
                StopTimedSending();
            }
        }

        private void UpdateDataDisplay()
        {
            try
            {
                // 系统状态和基本信息
                ProcotolUsedTextBox.Text = receivedData.ProcotolUsed.ToString();
                DateCodeTextBox.Text = receivedData.DateCode.ToString();
                MdlPrjCodeTextBox.Text = receivedData.MdlPrjCode.ToString();
                IsrLoadRatioTextBox.Text = receivedData.IsrLoadRatio.ToString("F3");
                AllLoadRatioTextBox.Text = receivedData.AllLoadRatio.ToString("F3");
                PwmTsTextBox.Text = receivedData.PwmTs.ToString("F2");

                // 错误和故障状态
                ErrNumTextBox.Text = receivedData.ErrNum.ToString();
                ErrRnkTextBox.Text = receivedData.ErrRnk.ToString();
                ErrSumTextBox.Text = receivedData.ErrSum.ToString();
                PwrLimKTextBox.Text = receivedData.PwrLimK.ToString("F2");
                ThrottleFailTextBox.Text = receivedData.ThrottleFail.ToString();
                BrakeFailTextBox.Text = receivedData.BrakeFail.ToString();
                SignalFailTextBox.Text = receivedData.SignalFail.ToString();

                // 电压参数
                UdcActTextBox.Text = receivedData.UdcAct.ToString("F2");
                UsOutTextBox.Text = receivedData.UsOut.ToString("F2");
                BrakeVoltFiltedTextBox.Text = receivedData.BrakeVoltFilted.ToString("F2");
                ThrottleVoltFiltedTextBox.Text = receivedData.ThrottleVoltFilted.ToString("F2");

                // 电流参数
                IuActTextBox.Text = receivedData.IuAct.ToString("F2");
                IvActTextBox.Text = receivedData.IvAct.ToString("F2");
                IwActTextBox.Text = receivedData.IwAct.ToString("F2");
                IdFbTextBox.Text = receivedData.IdFb.ToString("F2");
                IqFbTextBox.Text = receivedData.IqFb.ToString("F2");
                IdCmdTextBox.Text = receivedData.IdCmd.ToString("F2");
                IqCmdTextBox.Text = receivedData.IqCmd.ToString("F2");
                WeakenCurtTextBox.Text = receivedData.WeakenCurt.ToString("F2");
                IdcFilterTextBox.Text = receivedData.IdcFilter.ToString("F2");

                // 电机状态参数
                FluxTextBox.Text = receivedData.Flux.ToString("F2");
                LdTextBox.Text = receivedData.Ld.ToString("F2");
                LqTextBox.Text = receivedData.Lq.ToString("F2");
                SpdCmdTextBox.Text = receivedData.SpdCmd.ToString("F2");
                MtrSpdTextBox.Text = receivedData.MtrSpd.ToString("F2");
                MtrStsTextBox.Text = receivedData.MtrSts.ToString();
                AscStsTextBox.Text = receivedData.AscSts.ToString();
                AglSdyStsTextBox.Text = receivedData.AglSdySts.ToString();
                InvModeTextBox.Text = receivedData.InvMode.ToString();
                CtrlModeTextBox.Text = receivedData.CtrlMode.ToString();
                ModulaionCoeffTextBox.Text = receivedData.ModulaionCoeff.ToString("F3");

                // 温度参数
                MtrT1TextBox.Text = receivedData.MtrT1.ToString("F1");
                MtrT2TextBox.Text = receivedData.MtrT2.ToString("F1");
                PwrMdlUTTextBox.Text = receivedData.PwrMdlUT.ToString("F1");

                // 扭矩控制参数
                TrqCmdTextBox.Text = receivedData.TrqCmd.ToString("F2");
                TrqEstTextBox.Text = receivedData.TrqEst.ToString("F2");
                TrqAbleMaxTextBox.Text = receivedData.TrqAbleMax.ToString("F2");
                TrqAbleMinTextBox.Text = receivedData.TrqAbleMin.ToString("F2");
                DriveTrqTextBox.Text = receivedData.DriveTrq.ToString("F2");
                KERS_TrqTextBox.Text = receivedData.KERS_Trq.ToString("F2");

                // 电压控制参数
                PidUdTextBox.Text = receivedData.PidUd.ToString("F2");
                PidUqTextBox.Text = receivedData.PidUq.ToString("F2");
                UdFfdTextBox.Text = receivedData.UdFfd.ToString("F2");
                UqFfdTextBox.Text = receivedData.UqFfd.ToString("F2");

                // 整车参数
                KeySwitchTextBox.Text = receivedData.KeySwitch.ToString();
                BrakeSigTextBox.Text = receivedData.BrakeSig.ToString();
                ThrottleOpeningTextBox.Text = receivedData.ThrottleOpening.ToString("F1");
                BrakeOpeningTextBox.Text = receivedData.BrakeOpening.ToString("F1");
                SigOptModReqTextBox.Text = receivedData.SigOptModReq.ToString();
                GearTextBox.Text = receivedData.Gear.ToString();
                ThreeGearSigTextBox.Text = receivedData.ThreeGearSig.ToString();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"更新显示错误: {ex.Message}";
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!serialPort.IsOpen)
                {
                    string portName = ComPortComboBox.SelectedItem?.ToString();
                    if (string.IsNullOrEmpty(portName))
                    {
                        StatusTextBox.Text = "请选择串口";
                        return;
                    }

                    serialPort.PortName = portName;

                    var selectedBaudRate = (BaudRateComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
                    if (string.IsNullOrEmpty(selectedBaudRate) || !int.TryParse(selectedBaudRate, out int baudRate))
                    {
                        baudRate = 115200;
                    }

                    serialPort.BaudRate = baudRate;
                    serialPort.Parity = Parity.None;
                    serialPort.DataBits = 8;
                    serialPort.StopBits = StopBits.One;
                    serialPort.ReadTimeout = 1000;
                    serialPort.WriteTimeout = 1000;
                    serialPort.Handshake = Handshake.None;
                    serialPort.RtsEnable = false;

                    serialPort.Open();

                    // 清空缓冲区
                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();

                    uiTimer.Start();
                    ConnectButton.Content = "关闭串口";
                    StatusTextBox.Text = $"已连接 {portName}";

                    ComPortComboBox.IsEnabled = false;
                    BaudRateComboBox.IsEnabled = false;

                    receiveBuffer.Clear();
                    receivedframeCount = 0;
                    FrameCountTextBox.Text = "0";
                    sendFrameCount = 0;
                    SendFrameCountTextBox.Text = "0";
                }
                else
                {
                    // 关闭连接
                    if (isSendingData)
                    {
                        StopTimedSending();
                    }

                    serialPort.Close();
                    uiTimer.Stop();
                    dataTimer.Stop();
                    ConnectButton.Content = "打开串口";
                    StatusTextBox.Text = "未连接";

                    ComPortComboBox.IsEnabled = true;
                    BaudRateComboBox.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                StatusTextBox.Text = $"错误: {ex.Message}";
                StatusText.Text = $"连接错误: {ex.Message}";

                ComPortComboBox.IsEnabled = true;
                BaudRateComboBox.IsEnabled = true;
            }
        }

        private void ComPortComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (serialPort.IsOpen)
            {
                if (isSendingData)
                {
                    StopTimedSending();
                }

                serialPort.Close();
                uiTimer.Stop();
                dataTimer.Stop();
                ConnectButton.Content = "打开串口";
                StatusTextBox.Text = "未连接";
                StatusText.Text = "已更改串口配置，请重新连接";
            }
        }

        private Border CreateVerticalArrayGroupControl(ArrayGroup arrayGroup)
        {
            var border = new Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 0),
                Padding = new Thickness(2),
                Background = System.Windows.Media.Brushes.WhiteSmoke
            };

            var mainStackPanel = new StackPanel();

            var arrayHeader = new TextBlock
            {
                Text = $"{arrayGroup.DisplayName}",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 2),
                Background = System.Windows.Media.Brushes.LightBlue,
                Padding = new Thickness(2),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = CreateVariableToolTip(new VehicleParameter
                {
                    VariableName = arrayGroup.BaseName,
                    DataType = arrayGroup.DataType,
                    DisplayName = arrayGroup.DisplayName
                })
            };
            mainStackPanel.Children.Add(arrayHeader);

            var grid = new Grid();

            int columns = arrayGroup.ArraySize <= 30 ? arrayGroup.ArraySize : 30;
            int rows = (int)Math.Ceiling((double)arrayGroup.ArraySize / columns);

            for (int i = 0; i < columns; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            }

            // 添加行定义
            for (int i = 0; i < rows; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            // 填充数组元素
            for (int i = 0; i < arrayGroup.ArraySize; i++)
            {
                int row = i / columns;
                int col = i % columns;

                var elementBorder = new Border();

                var elementStackPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var parameter = arrayGroup.Parameters.FirstOrDefault(p => p.ArrayIndex == i);

                var indexTextBlock = new TextBlock
                {
                    Text = $"{i}",
                    FontSize = 9,
                    Foreground = System.Windows.Media.Brushes.DarkBlue,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0, 0, 0)
                };

                string currentValue = parameter?.CurrentValue ?? "0";

                var valueTextBox = new TextBox
                {
                    Text = currentValue,
                    FontSize = 12,
                    Tag = parameter,
                    Width = 50,
                    Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsReadOnly = false
                };

                valueTextBox.TextChanged += (s, e) =>
                {
                    if (parameter != null)
                    {
                        parameter.CurrentValue = valueTextBox.Text;
                    }
                };

                valueTextBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter && parameter != null)
                    {
                        AutoSendParameter(parameter);
                    }
                };

                elementStackPanel.Children.Add(indexTextBlock);
                elementStackPanel.Children.Add(valueTextBox);
                elementBorder.Child = elementStackPanel;

                Grid.SetRow(elementBorder, row);
                Grid.SetColumn(elementBorder, col);
                grid.Children.Add(elementBorder);
            }

            mainStackPanel.Children.Add(grid);

            border.Child = mainStackPanel;
            return border;
        }

        private void GroupParameters()
        {
            _parameterGroups = new Dictionary<string, List<VehicleParameter>>();

            var nonArrayParameters = _parameters
                .Where(p => p.ArraySize == 1)
                .OrderBy(p => p.Index)
                .ToList();

            int groupSize = 12;
            int totalGroups = (int)Math.Ceiling((double)nonArrayParameters.Count / groupSize);

            for (int i = 0; i < totalGroups; i++)
            {
                int startIndex = i * groupSize + 1;
                int endIndex = Math.Min((i + 1) * groupSize, nonArrayParameters.Count);
                string groupName = $"分组{i + 1}（{startIndex}-{endIndex}）";

                var currentGroupParams = nonArrayParameters
                    .Skip(i * groupSize)
                    .Take(groupSize)
                    .ToList();

                _parameterGroups.Add(groupName, currentGroupParams);
            }
        }

        private void GroupArrayParameters()
        {
            _arrayGroups = new List<ArrayGroup>();

            var arrayParameters = _parameters.Where(p => p.ArraySize > 1).ToList();

            var arrayGroups = arrayParameters
                .GroupBy(p => p.VariableName)
                .Where(g => !string.IsNullOrEmpty(g.Key));

            foreach (var group in arrayGroups)
            {
                var firstParam = group.First();
                var arrayGroup = new ArrayGroup
                {
                    BaseName = group.Key,
                    DisplayName = firstParam.DisplayName, // 移除索引部分
                    ArraySize = group.First().ArraySize,
                    DataType = group.First().DataType,
                    Parameters = group.OrderBy(p => p.ArrayIndex).ToList()
                };

                _arrayGroups.Add(arrayGroup);
            }

            _arrayGroups = _arrayGroups.OrderBy(g => g.DisplayName).ToList();
        }

        private void CreateArrayLookupPage()
        {
            var tabPage = new TabItem
            {
                Header = "数组查表",
                Style = (Style)FindResource(typeof(TabItem)) ?? new Style(typeof(TabItem))
            };

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var mainStackPanel = new StackPanel
            {
                Margin = new Thickness(10),
                Orientation = Orientation.Vertical
            };

            if (_arrayGroups == null || _arrayGroups.Count == 0)
            {
                mainStackPanel.Children.Add(new TextBlock
                {
                    Text = "暂无数组参数数据",
                    FontSize = 14,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                });
            }
            else
            {
                foreach (var arrayGroup in _arrayGroups)
                {
                    var arrayControl = CreateVerticalArrayGroupControl(arrayGroup);
                    mainStackPanel.Children.Add(arrayControl);
                }
            }

            scrollViewer.Content = mainStackPanel;
            tabPage.Content = scrollViewer;
            MainTabControl.Items.Add(tabPage);
        }

        private void SaveConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveFileDialog saveDialog = new SaveFileDialog
                {
                    Filter = "配置文件|*.json|所有文件|*.*",
                    Title = "保存配置"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    bool success = JsonConfigManager.SaveConfigToFile(_parameters, saveDialog.FileName);
                    if (success)
                    {
                        MessageBox.Show("配置保存成功!", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                        StatusText.Text = "配置已保存";
                    }
                    else
                    {
                        MessageBox.Show("配置保存失败!", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        StatusText.Text = "保存失败";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "保存失败";
            }
        }

        private void LoadConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenFileDialog openDialog = new OpenFileDialog
                {
                    Filter = "配置文件|*.json|所有文件|*.*",
                    Title = "加载配置文件到上位机"
                };

                if (openDialog.ShowDialog() == true)
                {
                    var loadedParameters = JsonConfigManager.LoadConfigFromFile(openDialog.FileName);
                    if (loadedParameters.Any())
                    {
                        // 清空现有参数
                        _parameters.Clear();
                        _parameterGroups.Clear();
                        _arrayGroups.Clear();

                        // 加载新参数
                        foreach (var loadedParam in loadedParameters)
                        {
                            var newParam = new VehicleParameter
                            {
                                Index = loadedParam.Index,
                                VariableName = loadedParam.VariableName,
                                DisplayName = loadedParam.DisplayName,
                                DataType = loadedParam.DataType,
                                CurrentValue = loadedParam.CurrentValue,
                                ArraySize = loadedParam.ArraySize,
                                ArrayIndex = loadedParam.ArrayIndex,
                            };
                            _parameters.Add(newParam);
                        }

                        // 分组参数
                        GroupParameters();
                        GroupArrayParameters();

                        // 重新创建参数显示页面
                        RefreshParameterDisplays();

                        // 更新状态
                        StatusText.Text = $"配置文件已加载到上位机 ({loadedParameters.Count} 个参数)";
                        MessageBox.Show($"配置文件已成功加载到上位机！\n\n共加载 {loadedParameters.Count} 个参数\n数组分组 {_arrayGroups.Count} 个",
                            "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("配置文件为空或格式错误!", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        StatusText.Text = "加载失败";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "加载失败";
            }
        }

        private async void DownloadToControllerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!serialPort.IsOpen)
                {
                    MessageBox.Show("请先打开串口连接！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusText.Text = "串口未连接，无法下载到控制器";
                    return;
                }

                if (_parameters == null || !_parameters.Any())
                {
                    MessageBox.Show("请先加载配置文件到上位机！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusText.Text = "没有参数数据，请先加载配置文件";
                    return;
                }

                var waitingDialog = new WaitingDialog();
                waitingDialog.Owner = this;
                waitingDialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                string result = "";
                var sendTask = Task.Run(async () =>
                {
                    return await SendAllParametersWithProgress(_parameters, waitingDialog);
                });

                waitingDialog.ShowDialog();

                result = await sendTask;

                MessageBox.Show($"数据下载到控制器完成！\n\n{result}",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                StatusText.Text = "数据已下载到控制器";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"下载失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "下载失败";
            }
        }

        private async Task<string> SendAllParametersWithProgress(List<VehicleParameter> parameters, WaitingDialog waitingDialog)
        {
            if (!serialPort.IsOpen)
            {
                waitingDialog.Close();
                return "串口未连接，无法发送配置给控制器！";
            }

            try
            {
                if (isSendingData)
                {
                    StopTimedSending();
                    await Task.Delay(200);
                }

                var sortedParameters = parameters.OrderBy(p => p.Index).ToList();
                int successCount = 0;
                int failedCount = 0;
                var failedParameters = new List<(string ParameterName, int Index, string Reason)>();

                DateTime startTime = DateTime.Now;
                TimeSpan timeout = TimeSpan.FromSeconds(20);
                bool isTimeout = false;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    waitingDialog.SetStatus($"正在将数据下载到控制器... 0/{sortedParameters.Count}");
                });

                for (int i = 0; i < sortedParameters.Count; i++)
                {
                    if (DateTime.Now - startTime > timeout)
                    {
                        isTimeout = true;
                        break;
                    }

                    var parameter = sortedParameters[i];

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        waitingDialog.SetStatus($"正在将数据下载到控制器... {i + 1}/{sortedParameters.Count}");
                    });

                    await Task.Delay(10);

                    bool parameterSuccess = false;
                    string failureReason = "";
                    int retryCount = 0;
                    const int maxRetryCount = 3;

                    while (!parameterSuccess && retryCount < maxRetryCount)
                    {
                        try
                        {
                            if (!serialPort.IsOpen)
                            {
                                failureReason = "串口连接已断开";
                                break;
                            }

                            if (retryCount > 0)
                            {
                                await Task.Delay(50);
                            }

                            bool sendSuccess = SendSingleParameter(parameter.Index, parameter.CurrentValue, 1);

                            if (sendSuccess)
                            {
                                bool ackSuccess = WaitForAck();

                                if (ackSuccess)
                                {
                                    parameterSuccess = true;
                                    successCount++;
                                }
                                else
                                {
                                    failureReason = "确认失败";
                                    retryCount++;
                                }
                            }
                            else
                            {
                                failureReason = "发送失败";
                                retryCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failureReason = $"异常: {ex.Message}";
                            retryCount++;
                            await Task.Delay(50);
                        }

                        await Task.Delay(10);
                    }

                    if (!parameterSuccess)
                    {
                        failedCount++;
                        failedParameters.Add((parameter.DisplayName, parameter.Index, failureReason));
                    }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    waitingDialog.Close();
                });

                string resultMessage;

                if (isTimeout)
                {
                    resultMessage = $"数据下载到控制器超时！\n成功: {successCount} 个\n失败: {failedCount} 个\n剩余参数未发送";

                    if (failedCount > 0)
                    {
                        resultMessage += $"\n失败的参数 ({failedCount}个):";
                        foreach (var failedParam in failedParameters.Take(5))
                        {
                            resultMessage += $"\n- {failedParam.ParameterName} (Index: {failedParam.Index})";
                        }

                        if (failedParameters.Count > 5)
                        {
                            resultMessage += $"\n... 还有 {failedParameters.Count - 5} 个失败的参数";
                        }
                    }
                }
                else
                {
                    resultMessage = $"数据已下载到控制器！\n成功: {successCount} 个\n失败: {failedCount} 个";

                    if (failedCount > 0)
                    {
                        resultMessage += $"\n\n失败的参数 ({failedCount}个):";
                        foreach (var failedParam in failedParameters.Take(5))
                        {
                            resultMessage += $"\n- {failedParam.ParameterName} (Index: {failedParam.Index})";
                        }

                        if (failedParameters.Count > 5)
                        {
                            resultMessage += $"\n... 还有 {failedParameters.Count - 5} 个失败的参数";
                        }
                    }
                }

                return resultMessage;
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    waitingDialog.Close();
                });
                return $"数据下载到控制器失败: {ex.Message}";
            }
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "软件版本：V2.1\n" +
                "版权所有 © 2025 上海盘毂动力科技股份有限公司\n",
                "关于",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ContactMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "自研上位机Beta版本，敬请期待Release版本\n" + "有问题或提出建议请联系电控研发部\n",
                "联系我们",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void RefreshParameterDisplays()
        {
            MainTabControl.Items.Clear();

            if (_parameters == null || !_parameters.Any())
            {
                CreateEmptyParameterPage();
            }
            else
            {
                if (_parameterGroups != null && _parameterGroups.Any())
                {
                    CreateNonArrayTabPage();
                }
                else
                {
                    var emptyTabItem = new TabItem
                    {
                        Header = "非数组参数",
                        Content = CreateEmptyArrayPage(),
                        Style = (Style)FindResource(typeof(TabItem)) ?? new Style(typeof(TabItem))
                    };
                    MainTabControl.Items.Add(emptyTabItem);
                }

                if (_arrayGroups != null && _arrayGroups.Any())
                {
                    CreateArrayLookupPage();
                }
                else
                {
                    var arrayTabItem = new TabItem
                    {
                        Header = "数组查表",
                        Content = CreateEmptyArrayPage(),
                        Style = (Style)FindResource(typeof(TabItem)) ?? new Style(typeof(TabItem))
                    };
                    MainTabControl.Items.Add(arrayTabItem);
                }
            }

            if (MainTabControl.Items.Count > 0)
            {
                MainTabControl.SelectedIndex = 0;
            }
        }

        private void ReadControllerDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (!serialPort.IsOpen)
            {
                MessageBox.Show("请先打开串口连接！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_parameters == null || !_parameters.Any())
            {
                MessageBox.Show("请先加载配置文件到上位机！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText.Text = "没有参数数据，请先加载配置文件";
                return;
            }

            try
            {
                // 停止定时发送
                if (isSendingData)
                {
                    StopTimedSending();
                    Thread.Sleep(200);
                }

                SendSingleParameter(READ_CAL_CMD, "0", 0);

                _waitingForParamData = true;

                StatusText.Text = "正在读取控制器数据...";

                // 设置超时定时器
                var timeoutTimer = new System.Timers.Timer(5000);
                timeoutTimer.Elapsed += (s, ev) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_waitingForParamData)
                        {
                            _waitingForParamData = false;
                            StatusText.Text = "读取超时，请重试";
                            MessageBox.Show("读取控制器数据超时！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    });
                };
                timeoutTimer.AutoReset = false;
                timeoutTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送读取请求失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "发送失败";
            }
        }

        private void GuJianShenJi_Click(object sender, RoutedEventArgs e)
        {
            if (serialPort.IsOpen)
            {
                _isFirmwareUpgrading = true;

                // 清空接收缓冲区
                serialPort.DiscardInBuffer();

                /* 打开bin文件 */
                OpenFileDialog OpenBin = new OpenFileDialog();
                OpenBin.Filter = "Bin文件|*.bin";
                OpenBin.ValidateNames = true;
                OpenBin.CheckFileExists = true;
                OpenBin.CheckPathExists = true;

                if (OpenBin.ShowDialog() == true)
                {
                    PathText = OpenBin.FileName;

                    if (string.IsNullOrEmpty(PathText))
                    {
                        MessageBox.Show("Bin文件路径不能为空！", "警告", MessageBoxButton.OK);
                        _isFirmwareUpgrading = false;  // 重置标志
                        return;
                    }
                    else
                    {
                        byte[] sendData = new byte[8] { 0xF4, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4F };
                        serialPort.Write(sendData, 0, sendData.Length);

                        ModbusDataReceiver.UpgradeBox Upgradewin = new ModbusDataReceiver.UpgradeBox();


                        Upgradewin.Owner = this;
                        Upgradewin.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                        Upgradewin.Closed += (s, args) =>
                        {
                            _isFirmwareUpgrading = false;

                            if (serialPort.IsOpen)
                            {
                                serialPort.DiscardInBuffer();
                                serialPort.DiscardOutBuffer();

                                receiveBuffer.Clear();

                                if (!uiTimer.IsEnabled && serialPort.IsOpen)
                                {
                                    uiTimer.Start();
                                }
                            }

                            this.Activate();
                            this.Focus();

                            // 更新状态显示
                            StatusText.Text = "固件升级完成";
                            StatusTextBox.Text = "固件升级完成，串口已准备好";
                        };

                        Upgradewin.ShowDialog();
                    }
                }
                else
                {
                    _isFirmwareUpgrading = false;
                }
            }
            else
            {
                MessageBox.Show("请先打开串口！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ParseDataToVehicleParameters(byte[] data)
        {
            try
            {
                int size = Marshal.SizeOf(typeof(VehicleParameters_t));

                if (data.Length < size)
                {
                    Dispatcher.Invoke(() => StatusText.Text = "参数数据长度不足");
                    return;
                }

                IntPtr ptr = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.Copy(data, 0, ptr, size);
                    _vehicleParameters = (VehicleParameters_t)Marshal.PtrToStructure(ptr, typeof(VehicleParameters_t));

                    // 更新参数列表
                    UpdateParametersFromVehicleData();

                    _waitingForParamData = false;

                    receivedframeCount ++;
                    Dispatcher.Invoke(() =>
                    {
                        FrameCountTextBox.Text = receivedframeCount.ToString();
                        StatusText.Text = "控制器数据读取成功";
                        MessageBox.Show("控制器参数数据已成功读取并更新！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    });
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"参数解析错误: {ex.Message}";
                    MessageBox.Show($"解析参数数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private void UpdateParametersFromVehicleData()
        {
            if (_parameters == null) return;

            try
            {
                // 更新非数组参数
                foreach (var param in _parameters.Where(p => p.ArraySize == 1))
                {
                    switch (param.VariableName)
                    {
                        // 空气阻力参数
                        case "VehCan_AirResistanceCoeff":
                            param.CurrentValue = _vehicleParameters.VehCan_AirResistanceCoeff.ToString("F4");
                            break;

                        // 限流功能参数
                        case "VehCan_Driver_En":
                            param.CurrentValue = _vehicleParameters.VehCan_Driver_En.ToString("F0");
                            break;
                        case "VehCan_Driver_EnSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_Driver_EnSetVal.ToString("F4");
                            break;

                        // 一键超车参数
                        case "VehCan_DrvA_AccelCnt":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_AccelCnt.ToString();
                            break;
                        case "VehCan_DrvA_AccelFlgEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_AccelFlgEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_AccelFlgSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_AccelFlgSetVal.ToString("F0");
                            break;
                        case "VehCan_DrvA_AccelSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_AccelSpd.ToString("F4");
                            break;

                        // 防盗模式参数
                        case "VehCan_DrvA_AntiTheftAllowSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_AntiTheftAllowSpd.ToString("F4");
                            break;

                        // 巡航模式参数
                        case "VehCan_DrvA_CruiseCntEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_CruiseCntEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_CruiseFlgEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_CruiseFlgEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_CruiseFlgSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_CruiseFlgSetVal.ToString("F0");
                            break;

                        // 驾驶状态选择
                        case "VehCan_DrvA_DriveMode":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_DriveMode.ToString();
                            break;

                        // 电子刹车参数
                        case "VehCan_DrvA_ElecBrkCnt":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ElecBrkCnt.ToString();
                            break;
                        case "VehCan_DrvA_ElecBrkFlgEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ElecBrkFlgEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_ElecBrkFlgSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ElecBrkFlgSetVal.ToString("F0");
                            break;

                        // Park模式参数
                        case "VehCan_DrvA_ParkAllowSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ParkAllowSpd.ToString("F4");
                            break;
                        case "VehCan_DrvA_ParkModeFlgEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ParkModeFlgEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_ParkModeFlgSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ParkModeFlgSetVal.ToString("F0");
                            break;
                        case "VehCan_DrvA_ParkToFwdSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ParkToFwdSpd.ToString("F4");
                            break;

                        // 推车助力参数
                        case "VehCan_DrvA_PushAssFlgEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_PushAssFlgEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_PushAssFlgSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_PushAssFlgSetVal.ToString("F0");
                            break;
                        case "VehCan_DrvA_PushAssSafeAllowSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_PushAssSafeAllowSpd.ToString("F4");
                            break;
                        case "VehCan_DrvA_PushAssTrq":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_PushAssTrq.ToString("F4");
                            break;

                        // 修复模式参数
                        case "VehCan_DrvA_RepairFlgEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_RepairFlgEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_RepairFlgSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_RepairFlgSetVal.ToString("F0");
                            break;
                        case "VehCan_DrvA_RepairSpdLim":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_RepairSpdLim.ToString("F4");
                            break;
                        case "VehCan_DrvA_RepairSpdRamp":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_RepairSpdRamp.ToString("F4");
                            break;

                        // 倒车模式参数
                        case "VehCan_DrvA_ReverseFlgEn":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ReverseFlgEn.ToString("F0");
                            break;
                        case "VehCan_DrvA_ReverseFlgSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ReverseFlgSetVal.ToString("F0");
                            break;
                        case "VehCan_DrvA_ReverseMaxSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ReverseMaxSpd.ToString("F4");
                            break;
                        case "VehCan_DrvA_ReverseTrqCoeff":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ReverseTrqCoeff.ToString("F4");
                            break;

                        // 转速模式参数
                        case "VehCan_DrvA_SigVehTrqMax":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_SigVehTrqMax.ToString("F4");
                            break;

                        // 扭矩斜率参数
                        case "VehCan_DrvA_SubTrqRamp":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_SubTrqRamp.ToString("F4");
                            break;

                        // 巡航模式参数
                        case "VehCan_DrvA_ToCruiseCnt":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ToCruiseCnt.ToString();
                            break;
                        case "VehCan_DrvA_ToCruiseSpdErr":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ToCruiseSpdErr.ToString("F4");
                            break;

                        // Park模式参数
                        case "VehCan_DrvA_ToParkWaitCnt":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ToParkWaitCnt.ToString();
                            break;

                        // 推车助力参数
                        case "VehCan_DrvA_ToPushAssSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_DrvA_ToPushAssSpd.ToString("F4");
                            break;

                        // 巡航模式参数
                        case "VehCan_Drva_ToCruiseSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_Drva_ToCruiseSpd.ToString("F4");
                            break;

                        // 能量回收模拟量输出参数
                        case "VehCan_KERS_AnalogBrkConsTrqEn":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_AnalogBrkConsTrqEn.ToString("F0");
                            break;
                        case "VehCan_KERS_AnalogBrkConsTrqSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_AnalogBrkConsTrqSetVal.ToString("F4");
                            break;

                        // 能量回收PI控制器参数
                        case "VehCan_KERS_IdcKi":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_IdcKi.ToString("F4");
                            break;
                        case "VehCan_KERS_IdcKp":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_IdcKp.ToString("F4");
                            break;
                        case "VehCan_KERS_IdcLim":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_IdcLim.ToString("F4");
                            break;

                        // 能量回收母线电压限制参数
                        case "VehCan_KERS_UdcKi":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_UdcKi.ToString("F4");
                            break;
                        case "VehCan_KERS_UdcKp":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_UdcKp.ToString("F4");
                            break;
                        case "VehCan_KERS_UdcLim":
                            param.CurrentValue = _vehicleParameters.VehCan_KERS_UdcLim.ToString("F4");
                            break;

                        // 电机减速器速比
                        case "VehCan_MtrReductionRatio":
                            param.CurrentValue = _vehicleParameters.VehCan_MtrReductionRatio.ToString("F4");
                            break;

                        // P档模式参数
                        case "VehCan_ParkSigEn":
                            param.CurrentValue = _vehicleParameters.VehCan_ParkSigEn.ToString("F0");
                            break;
                        case "VehCan_ParkSigSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_ParkSigSetVal.ToString("F0");
                            break;

                        // 刹车信号处理参数
                        case "VehCan_SigOpt_AnaBrkValid":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_AnaBrkValid.ToString("F0");
                            break;
                        case "VehCan_SigOpt_BrkFilterPara":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkFilterPara.ToString("F4");
                            break;
                        case "VehCan_SigOpt_BrkGain":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkGain.ToString("F4");
                            break;
                        case "VehCan_SigOpt_BrkOCV":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkOCV.ToString("F4");
                            break;
                        case "VehCan_SigOpt_BrkSCV":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkSCV.ToString("F4");
                            break;
                        case "VehCan_SigOpt_BrkSigEn":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkSigEn.ToString("F0");
                            break;
                        case "VehCan_SigOpt_BrkSigSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkSigSetVal.ToString("F0");
                            break;
                        case "VehCan_SigOpt_BrkVmax":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkVmax.ToString("F4");
                            break;
                        case "VehCan_SigOpt_BrkVmin":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_BrkVmin.ToString("F4");
                            break;

                        // 油门信号处理参数
                        case "VehCan_SigOpt_ThrFilterPara":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrFilterPara.ToString("F4");
                            break;
                        case "VehCan_SigOpt_ThrGain":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrGain.ToString("F4");
                            break;
                        case "VehCan_SigOpt_ThrOCV":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrOCV.ToString("F4");
                            break;
                        case "VehCan_SigOpt_ThrOpnEn":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrOpnEn.ToString("F0");
                            break;
                        case "VehCan_SigOpt_ThrOpnSetVal":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrOpnSetVal.ToString("F4");
                            break;
                        case "VehCan_SigOpt_ThrSCV":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrSCV.ToString("F4");
                            break;
                        case "VehCan_SigOpt_ThrVmax":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrVmax.ToString("F4");
                            break;
                        case "VehCan_SigOpt_ThrVmin":
                            param.CurrentValue = _vehicleParameters.VehCan_SigOpt_ThrVmin.ToString("F4");
                            break;

                        // 驻坡模式开关
                        case "VehCan_SigVehHillHoldEn":
                            param.CurrentValue = _vehicleParameters.VehCan_SigVehHillHoldEn.ToString("F0");
                            break;

                        case "VehCan_ThreeGearMode":
                            param.CurrentValue = _vehicleParameters.VehCan_ThreeGearMode.ToString("F0");
                            break;

                        // 整车模块周期
                        case "VehCan_Ts":
                            param.CurrentValue = _vehicleParameters.VehCan_Ts.ToString("F4");
                            break;

                        // 母线电压滤波参数
                        case "VehCan_UdcFilterPara":
                            param.CurrentValue = _vehicleParameters.VehCan_UdcFilterPara.ToString("F4");
                            break;

                        // 整车控制模式
                        case "VehCan_VehCtrlMode":
                            param.CurrentValue = _vehicleParameters.VehCan_VehCtrlMode.ToString();
                            break;

                        // 机械刹车及电制动参数
                        case "VehCan_VehDigitalBrakeTrqGain":
                            param.CurrentValue = _vehicleParameters.VehCan_VehDigitalBrakeTrqGain.ToString("F4");
                            break;

                        // 摩擦力、重力分力参数
                        case "VehCan_VehExternalForceInitVal":
                            param.CurrentValue = _vehicleParameters.VehCan_VehExternalForceInitVal.ToString("F4");
                            break;
                        case "VehCan_VehExternalForceInitValCalSpd":
                            param.CurrentValue = _vehicleParameters.VehCan_VehExternalForceInitValCalSpd.ToString("F4");
                            break;
                        case "VehCan_VehMassForceFilterPara":
                            param.CurrentValue = _vehicleParameters.VehCan_VehMassForceFilterPara.ToString("F4");
                            break;
                        case "VehCan_VehMaxMass":
                            param.CurrentValue = _vehicleParameters.VehCan_VehMaxMass.ToString("F4");
                            break;
                        case "VehCan_VehMinMass":
                            param.CurrentValue = _vehicleParameters.VehCan_VehMinMass.ToString("F4");
                            break;

                        // 轮胎半径
                        case "VehCan_VehWheelRadius":
                            param.CurrentValue = _vehicleParameters.VehCan_VehWheelRadius.ToString("F4");
                            break;

                        default:
                            UpdateParameterByName(param);
                            break;
                    }
                }

                // 更新数组参数
                UpdateArrayParameters();

                // 刷新UI显示
                Dispatcher.Invoke(() =>
                {
                    RefreshParameterDisplays();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新参数失败: {ex.Message}");
            }
        }

        private void UpdateParameterByName(VehicleParameter param)
        {
            try
            {
                var field = typeof(VehicleParameters_t).GetField(param.VariableName);
                if (field != null)
                {
                    object value = field.GetValue(_vehicleParameters);
                    if (value != null)
                    {
                        if (field.FieldType == typeof(bool))
                            param.CurrentValue = ((bool)value) ? "1" : "0";
                        else if (field.FieldType == typeof(float))
                            param.CurrentValue = ((float)value).ToString("F4");
                        else if (field.FieldType == typeof(int) || field.FieldType == typeof(uint))
                            param.CurrentValue = value.ToString();
                        else if (field.FieldType == typeof(byte))
                            param.CurrentValue = ((byte)value).ToString();
                        else if (field.FieldType == typeof(ushort))
                            param.CurrentValue = ((ushort)value).ToString();
                    }
                }
            }
            catch
            {
                // 忽略映射错误
            }
        }

        // 更新数组参数
        private void UpdateArrayParameters()
        {
            if (_arrayGroups == null) return;

            foreach (var group in _arrayGroups)
            {
                foreach (var param in group.Parameters)
                {
                    try
                    {
                        if (param.ArrayIndex >= 0)
                        {
                            // 根据数组名和索引获取值
                            object value = GetArrayValueByName(param.VariableName, param.ArrayIndex);
                            if (value != null)
                            {
                                if (value is float)
                                    param.CurrentValue = ((float)value).ToString("F4");
                                else if (value is byte)
                                    param.CurrentValue = ((byte)value).ToString();
                                else if (value is bool)
                                    param.CurrentValue = ((bool)value) ? "1" : "0";
                                else
                                    param.CurrentValue = value.ToString();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"更新数组参数失败: {param.VariableName}, {ex.Message}");
                    }
                }
            }
        }


        // 根据数组名称获取对应的数组
        private object GetArrayValueByName(string arrayName, int index)
        {
            try
            {
                switch (arrayName)
                {
                    // 驾驶模式断点 (3个元素)
                    case "VehCan_DriverModeBp":
                        if (index >= 0 && index < _vehicleParameters.VehCan_DriverModeBp.Length)
                            return _vehicleParameters.VehCan_DriverModeBp[index];
                        break;

                    // 前进扭矩斜率查表 (9个元素)
                    case "VehCan_ForwardTrqRampMapdata":
                        if (index >= 0 && index < _vehicleParameters.VehCan_ForwardTrqRampMapdata.Length)
                            return _vehicleParameters.VehCan_ForwardTrqRampMapdata[index];
                        break;

                    // IdcKiMapData数组 (24个元素)
                    case "VehCan_IdcKiMapData":
                        if (index >= 0 && index < _vehicleParameters.VehCan_IdcKiMapData.Length)
                            return _vehicleParameters.VehCan_IdcKiMapData[index];
                        break;

                    // IdcKpKiMapBp数组 (24个元素)
                    case "VehCan_IdcKpKiMapBp":
                        if (index >= 0 && index < _vehicleParameters.VehCan_IdcKpKiMapBp.Length)
                            return _vehicleParameters.VehCan_IdcKpKiMapBp[index];
                        break;

                    // IdcKpMapData数组 (24个元素)
                    case "VehCan_IdcKpMapData":
                        if (index >= 0 && index < _vehicleParameters.VehCan_IdcKpMapData.Length)
                            return _vehicleParameters.VehCan_IdcKpMapData[index];
                        break;

                    // 能量回收扭矩系数查表 (25个元素)
                    case "VehCan_KERS_BrakeTrqGain":
                        if (index >= 0 && index < _vehicleParameters.VehCan_KERS_BrakeTrqGain.Length)
                            return _vehicleParameters.VehCan_KERS_BrakeTrqGain[index];
                        break;

                    // KERS_EcoModeMapDataSlide数组 (25个元素)
                    case "VehCan_KERS_EcoModeMapDataSlide":
                        if (index >= 0 && index < _vehicleParameters.VehCan_KERS_EcoModeMapDataSlide.Length)
                            return _vehicleParameters.VehCan_KERS_EcoModeMapDataSlide[index];
                        break;

                    // Normal模式滑行扭矩查表 (25个元素)
                    case "VehCan_KERS_NormalModeMapDataSlide":
                        if (index >= 0 && index < _vehicleParameters.VehCan_KERS_NormalModeMapDataSlide.Length)
                            return _vehicleParameters.VehCan_KERS_NormalModeMapDataSlide[index];
                        break;

                    // 能量回收转速断点 (25个元素)
                    case "VehCan_KERS_SpdBp":
                        if (index >= 0 && index < _vehicleParameters.VehCan_KERS_SpdBp.Length)
                            return _vehicleParameters.VehCan_KERS_SpdBp[index];
                        break;

                    // 最高转速限制查表 (9个元素)
                    case "VehCan_SpdMaxMapData":
                        if (index >= 0 && index < _vehicleParameters.VehCan_SpdMaxMapData.Length)
                            return _vehicleParameters.VehCan_SpdMaxMapData[index];
                        break;

                    // 油门响应系数查表 (11个元素)
                    case "VehCan_ThrRespMapDataEco":
                        if (index >= 0 && index < _vehicleParameters.VehCan_ThrRespMapDataEco.Length)
                            return _vehicleParameters.VehCan_ThrRespMapDataEco[index];
                        break;

                    case "VehCan_ThrRespMapDataNormal":
                        if (index >= 0 && index < _vehicleParameters.VehCan_ThrRespMapDataNormal.Length)
                            return _vehicleParameters.VehCan_ThrRespMapDataNormal[index];
                        break;

                    case "VehCan_ThrRespMapDataSport":
                        if (index >= 0 && index < _vehicleParameters.VehCan_ThrRespMapDataSport.Length)
                            return _vehicleParameters.VehCan_ThrRespMapDataSport[index];
                        break;

                    // ThrRespPctBp数组 (11个元素)
                    case "VehCan_ThrRespPctBp":
                        if (index >= 0 && index < _vehicleParameters.VehCan_ThrRespPctBp.Length)
                            return _vehicleParameters.VehCan_ThrRespPctBp[index];
                        break;

                    // 三速档位参数 (3个元素)
                    case "VehCan_ThreeGearBp":
                        if (index >= 0 && index < _vehicleParameters.VehCan_ThreeGearBp.Length)
                            return _vehicleParameters.VehCan_ThreeGearBp[index];
                        break;

                    // 扭矩限制系数查表 (9个元素)
                    case "VehCan_TrqMaxMapData":
                        if (index >= 0 && index < _vehicleParameters.VehCan_TrqMaxMapData.Length)
                            return _vehicleParameters.VehCan_TrqMaxMapData[index];
                        break;

                    // UdcMapBp数组 (24个元素)
                    case "VehCan_UdcMapBp":
                        if (index >= 0 && index < _vehicleParameters.VehCan_UdcMapBp.Length)
                            return _vehicleParameters.VehCan_UdcMapBp[index];
                        break;

                    // UdcMapData数组 (24个元素)
                    case "VehCan_UdcMapData":
                        if (index >= 0 && index < _vehicleParameters.VehCan_UdcMapData.Length)
                            return _vehicleParameters.VehCan_UdcMapData[index];
                        break;
                }
            }
            catch (Exception)
            {
                // 忽略异常
            }

            return null;
        }

        private void InitializeEmptyParameters()
        {
            _parameters = new List<VehicleParameter>();
            _parameterGroups = new Dictionary<string, List<VehicleParameter>>();
            _arrayGroups = new List<ArrayGroup>();

            // 创建一个空的分组显示页面
            CreateEmptyParameterPage();
        }

        // 创建空参数页面
        private void CreateEmptyParameterPage()
        {
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(1)
            };

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 50, 0, 0)
            };

            // 添加提示信息
            var infoText = new TextBlock
            {
                Text = "请加载配置文件以显示参数",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stackPanel.Children.Add(infoText);
            scrollViewer.Content = stackPanel;

            var nonArrayTabItem = new TabItem
            {
                Header = "非数组参数",
                Content = scrollViewer,
                Style = (Style)FindResource(typeof(TabItem)) ?? new Style(typeof(TabItem))
            };

            // 创建空的数组参数页面
            var arrayTabItem = new TabItem
            {
                Header = "数组查表",
                Content = CreateEmptyArrayPage(),
                Style = (Style)FindResource(typeof(TabItem)) ?? new Style(typeof(TabItem))
            };

            MainTabControl.Items.Clear();
            MainTabControl.Items.Add(nonArrayTabItem);
            MainTabControl.Items.Add(arrayTabItem);

            if (MainTabControl.Items.Count > 0)
            {
                MainTabControl.SelectedIndex = 0;
            }
        }

        // 创建空数组页面
        private UIElement CreateEmptyArrayPage()
        {
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 50, 0, 0)
            };

            var infoText = new TextBlock
            {
                Text = "请加载配置文件以显示参数",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stackPanel.Children.Add(infoText);
            scrollViewer.Content = stackPanel;

            return scrollViewer;
        }

    }


    public class ArrayGroup
    {
        public string BaseName { get; set; }
        public string DisplayName { get; set; }
        public int ArraySize { get; set; }
        public string DataType { get; set; }
        public List<VehicleParameter> Parameters { get; set; }

        public ArrayGroup()
        {
            Parameters = new List<VehicleParameter>();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BswIf_AppModbusToBswStr_Struct
    {
        // 系统状态和基本信息
        public byte ProcotolUsed;                // 协议状态
        public uint DateCode;                    // 日期代码
        public uint MdlPrjCode;                  // 项目代码
        public float IsrLoadRatio;               // ISR负载率
        public float AllLoadRatio;               // 总负载率
        public float PwmTs;                      // PWM周期

        // 错误和故障状态
        public byte ErrNum;                      // 错误编号
        public byte ErrRnk;                      // 错误等级
        public byte ErrSum;                      // 错误总和
        public byte ThrottleFail;                // 油门故障
        public byte BrakeFail;                   // 刹车故障
        public byte SignalFail;                  // 信号故障
        public float PwrLimK;                    // 限功率系数

        // 电压参数
        public float UdcAct;                     // 母线电压
        public float UsOut;                      // 输出电压
        public float BrakeVoltFilted;            // 刹车电压
        public float ThrottleVoltFilted;         // 油门电压

        // 电流参数
        public float IuAct;                      // U相电流
        public float IvAct;                      // V相电流
        public float IwAct;                      // W相电流
        public float IdFb;                       // D轴电流
        public float IqFb;                       // Q轴电流
        public float IdCmd;                      // D轴命令电流
        public float IqCmd;                      // Q轴命令电流
        public float WeakenCurt;                 // 弱磁电流
        public float IdcFilter;                  // 直流电流

        // 电机状态参数
        public float Flux;                       // 磁链
        public float Ld;                         // D轴电感
        public float Lq;                         // Q轴电感

        public float SpdCmd;                     // 速度命令
        public float MtrSpd;                     // 电机转速
        public byte MtrSts;                      // 电机状态
        public byte AscSts;                      // ASC状态
        public byte AglSdySts;                   // 磁编状态
        public byte InvMode;                     // 逆变器模式
        public byte CtrlMode;                    // 控制模式
        public float ModulaionCoeff;             // 调制系数

        // 温度参数
        public float MtrT1;                      // 电机温度1
        public float MtrT2;                      // 电机温度2
        public float PwrMdlUT;                   // MOS温度

        // 扭矩控制参数
        public float TrqCmd;                     // 扭矩命令
        public float TrqEst;                     // 扭矩估算值
        public float TrqAbleMax;                 // 最大可用扭矩
        public float TrqAbleMin;                 // 最小可用扭矩
        public float DriveTrq;                   // 驱动扭矩
        public float KERS_Trq;                   // 回收扭矩

        // 电压控制参数
        public float PidUd;                      // D轴电压
        public float PidUq;                      // Q轴电压
        public float UdFfd;                      // D轴前馈电压
        public float UqFfd;                      // Q轴前馈电压

        // 整车参数
        public byte KeySwitch;                   // 钥匙开关
        public byte BrakeSig;                    // 刹车信号
        public float ThrottleOpening;            // 油门开度
        public float BrakeOpening;               // 刹车开度
        public byte SigOptModReq;                // 整车模式
        public byte Gear;                        // 运动模式
        public byte ThreeGearSig;                // 三速档位
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VehicleParameters_t
    {
        // 空气阻力参数
        public float VehCan_AirResistanceCoeff;

        // 驾驶模式断点
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] VehCan_DriverModeBp;

        public byte VehCan_Driver_En;
        public float VehCan_Driver_EnSetVal;

        // 一键超车参数
        public ushort VehCan_DrvA_AccelCnt;
        public byte VehCan_DrvA_AccelFlgEn;
        public byte VehCan_DrvA_AccelFlgSetVal;
        public float VehCan_DrvA_AccelSpd;

        // 防盗模式参数
        public float VehCan_DrvA_AntiTheftAllowSpd;

        public byte VehCan_DrvA_CruiseCntEn;
        public byte VehCan_DrvA_CruiseFlgEn;
        public byte VehCan_DrvA_CruiseFlgSetVal;

        // 驾驶状态选择
        public byte VehCan_DrvA_DriveMode;

        // 电子刹车参数
        public ushort VehCan_DrvA_ElecBrkCnt;
        public byte VehCan_DrvA_ElecBrkFlgEn;
        public byte VehCan_DrvA_ElecBrkFlgSetVal;

        // Park模式参数
        public float VehCan_DrvA_ParkAllowSpd;
        public byte VehCan_DrvA_ParkModeFlgEn;
        public byte VehCan_DrvA_ParkModeFlgSetVal;
        public float VehCan_DrvA_ParkToFwdSpd;

        public byte VehCan_DrvA_PushAssFlgEn;
        public byte VehCan_DrvA_PushAssFlgSetVal;
        public float VehCan_DrvA_PushAssSafeAllowSpd;
        public float VehCan_DrvA_PushAssTrq;

        public byte VehCan_DrvA_RepairFlgEn;
        public byte VehCan_DrvA_RepairFlgSetVal;
        public float VehCan_DrvA_RepairSpdLim;
        public float VehCan_DrvA_RepairSpdRamp;

        public byte VehCan_DrvA_ReverseFlgEn;
        public byte VehCan_DrvA_ReverseFlgSetVal;
        public float VehCan_DrvA_ReverseMaxSpd;
        public float VehCan_DrvA_ReverseTrqCoeff;

        // 转速模式参数
        public float VehCan_DrvA_SigVehTrqMax;

        // 扭矩斜率参数
        public float VehCan_DrvA_SubTrqRamp;

        // 巡航模式参数
        public ushort VehCan_DrvA_ToCruiseCnt;
        public float VehCan_DrvA_ToCruiseSpdErr;

        // Park模式参数
        public uint VehCan_DrvA_ToParkWaitCnt;

        // 推车助力参数
        public float VehCan_DrvA_ToPushAssSpd;

        // 巡航模式参数
        public float VehCan_Drva_ToCruiseSpd;

        // 前进扭矩斜率查表
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public float[] VehCan_ForwardTrqRampMapdata;

        // IdcKiMapData数组 (24个元素)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public float[] VehCan_IdcKiMapData;

        // IdcKpKiMapBp数组 (24个元素)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public float[] VehCan_IdcKpKiMapBp;

        // IdcKpMapData数组 (24个元素)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public float[] VehCan_IdcKpMapData;

        public byte VehCan_KERS_AnalogBrkConsTrqEn;
        public float VehCan_KERS_AnalogBrkConsTrqSetVal;

        // 能量回收扭矩系数查表
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] VehCan_KERS_BrakeTrqGain;

        // KERS_EcoModeMapDataSlide数组 (25个元素)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] VehCan_KERS_EcoModeMapDataSlide;

        // 能量回收PI控制器参数
        public float VehCan_KERS_IdcKi;
        public float VehCan_KERS_IdcKp;
        public float VehCan_KERS_IdcLim;

        // Normal模式滑行扭矩查表
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] VehCan_KERS_NormalModeMapDataSlide;

        // 能量回收转速断点
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] VehCan_KERS_SpdBp;

        // 能量回收母线电压限制参数
        public float VehCan_KERS_UdcKi;
        public float VehCan_KERS_UdcKp;
        public float VehCan_KERS_UdcLim;

        // 电机减速器速比
        public float VehCan_MtrReductionRatio;

        public byte VehCan_ParkSigEn;
        public byte VehCan_ParkSigSetVal;

        public byte VehCan_SigOpt_AnaBrkValid;
        public float VehCan_SigOpt_BrkFilterPara;
        public float VehCan_SigOpt_BrkGain;
        public float VehCan_SigOpt_BrkOCV;
        public float VehCan_SigOpt_BrkSCV;
        public byte VehCan_SigOpt_BrkSigEn;
        public byte VehCan_SigOpt_BrkSigSetVal;
        public float VehCan_SigOpt_BrkVmax;
        public float VehCan_SigOpt_BrkVmin;

        // 油门信号处理参数
        public float VehCan_SigOpt_ThrFilterPara;
        public float VehCan_SigOpt_ThrGain;
        public float VehCan_SigOpt_ThrOCV;
        public byte VehCan_SigOpt_ThrOpnEn;
        public float VehCan_SigOpt_ThrOpnSetVal;
        public float VehCan_SigOpt_ThrSCV;
        public float VehCan_SigOpt_ThrVmax;
        public float VehCan_SigOpt_ThrVmin;

        public byte VehCan_SigVehHillHoldEn;

        // 最高转速限制查表
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public float[] VehCan_SpdMaxMapData;

        // 油门响应系数查表
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public float[] VehCan_ThrRespMapDataEco;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public float[] VehCan_ThrRespMapDataNormal;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public float[] VehCan_ThrRespMapDataSport;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public float[] VehCan_ThrRespPctBp;

        // 三速档位参数
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public byte[] VehCan_ThreeGearBp;
        public byte VehCan_ThreeGearMode;

        // 扭矩限制系数查表
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
        public float[] VehCan_TrqMaxMapData;

        // 整车模块周期
        public float VehCan_Ts;

        // 母线电压滤波参数
        public float VehCan_UdcFilterPara;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public float[] VehCan_UdcMapBp;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public float[] VehCan_UdcMapData;

        // 整车控制模式
        public byte VehCan_VehCtrlMode;

        // 机械刹车及电制动参数
        public float VehCan_VehDigitalBrakeTrqGain;

        // 摩擦力、重力分力参数
        public float VehCan_VehExternalForceInitVal;
        public float VehCan_VehExternalForceInitValCalSpd;
        public float VehCan_VehMassForceFilterPara;
        public float VehCan_VehMaxMass;
        public float VehCan_VehMinMass;

        // 轮胎半径  
        public float VehCan_VehWheelRadius;
    }
}