using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ModbusDataReceiver
{
    public partial class UpgradeBox : Window
    {
        Thread thread1;
        public enum InitialCrcValue { Zeros, NonZero1 = 0xffff, NonZero2 = 0x1D0F }
        DispatcherTimer Dtimer;
        DispatcherTimer statusTimer; // 新增：状态更新定时器

        int fileSizenum;
        int percent;
        ushort packetCount = 0;
        int totalSentBytes = 0;
        DateTime startTime;

        private string fileName = "";
        private string fileSizeStr = "";
        private long fileSizeBytes = 0;

        // 添加关闭按钮的Enabled属性
        public bool CloseButtonEnabled
        {
            get { return btnClose.IsEnabled; }
            set { btnClose.IsEnabled = value; }
        }

        public UpgradeBox()
        {
            InitializeComponent();

            // 初始化定时器
            Dtimer = new DispatcherTimer();
            statusTimer = new DispatcherTimer();

            // 升级超时定时器
            Dtimer.Interval = TimeSpan.FromSeconds(5);
            Dtimer.Tick += new EventHandler(timer_Tick);

            // 状态更新定时器（每秒更新一次）
            statusTimer.Interval = TimeSpan.FromSeconds(1);
            statusTimer.Tick += StatusTimer_Tick;

            // 初始化UI显示
            InitializeUI();

            // 确保串口是打开的
            if (MainWindow.serialPort != null && MainWindow.serialPort.IsOpen)
            {
                Thread.Sleep(1000);
                MainWindow.serialPort.DiscardInBuffer();
                thread1 = new Thread(YmodemUploadFile);
                thread1.Start();
            }
            else
            {
                UpdateStatusInfo("串口未打开，无法升级", "Red");
                CloseButtonEnabled = true;
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void InitializeUI()
        {
            try
            {
                // 获取文件信息
                string path = MainWindow.PathText;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    fileName = System.IO.Path.GetFileName(path);
                    FileInfo fileInfo = new FileInfo(path);
                    fileSizeBytes = fileInfo.Length;
                    fileSizeStr = FormatFileSize(fileSizeBytes);
                }

                // 更新UI
                UpdateStatusInfo("初始化中...", "Initializing");
                UpdateFileInfo();

                // 开始计时
                startTime = DateTime.Now;
                statusTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化UI失败: {ex.Message}");
            }
        }

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // 更新耗时
                TimeSpan elapsed = DateTime.Now - startTime;
                // 更新状态栏
                UpdateStatusBar(elapsed);
            }
            catch { }
        }

        private void UpdateStatusBar(TimeSpan elapsed)
        {
            try
            {
                string statusText = $"升级中 - 包: {packetCount:000} | 耗时: {elapsed.TotalSeconds:F1}秒";

                // 添加串口状态
                if (MainWindow.serialPort != null)
                {
                    if (MainWindow.serialPort.IsOpen)
                    {
                        statusText += " | 串口已连接";
                        int bytesToRead = MainWindow.serialPort.BytesToRead;
                    }
                    else
                    {
                        statusText += " | 串口断开";
                    }
                }
            }
            catch { }
        }

        private void UpdateFileInfo()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtFileName.Text = fileName;
                txtFileSize.Text = fileSizeStr;
            }));
        }

        private void UpdateStatusInfo(string status, string color = "Blue")
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtStatus.Text = status;

                // 根据状态设置颜色
                switch (color.ToLower())
                {
                    case "green":
                        txtStatus.Foreground = System.Windows.Media.Brushes.Green;
                        break;
                    case "red":
                        txtStatus.Foreground = System.Windows.Media.Brushes.Red;
                        break;
                    case "orange":
                        txtStatus.Foreground = System.Windows.Media.Brushes.Orange;
                        break;
                    default:
                        txtStatus.Foreground = System.Windows.Media.Brushes.Blue;
                        break;
                }
            }));
        }

        private void UpdatePacketInfo(int currentPacket, int totalPackets)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                packetCount = (ushort)currentPacket;

                // 计算百分比
                if (totalPackets > 0)
                {
                    percent = (currentPacket * 100) / totalPackets;
                    txtProgressPercent.Text = $"{percent}%";
                    progressBar1.Value = currentPacket;
                    progressBar1.Maximum = totalPackets;
                }
            }));
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double len = bytes;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return string.Format("{0:0.##} {1}", len, sizes[order]);
        }

        void timer_Tick(object sender, EventArgs e)
        {
            UpdateStatusInfo("超时", "Red");

            Dispatcher.BeginInvoke(new Action(() => {
                CloseButtonEnabled = true;
                if (thread1 != null && thread1.IsAlive)
                    thread1.Abort();
            }), null);

            statusTimer.Stop();
            Dtimer.Stop();
        }

        private void YmodemUploadFile()
        {
            const byte STX = 2;
            const byte EOT = 4;
            const byte ACK = 6;
            const byte NAK = 0x15;
            const byte CA = 0x18;
            const byte C = 0x43;
            int FUCK_C = 0;

            const int dataSize = 1024;
            const int crcSize = 2;
            byte packetNumber = 0;
            int n = 0;
            int invertedPacketNumber = 255;
            byte[] databin = new byte[dataSize];
            byte[] CRC = new byte[crcSize];
            byte[] sendData = new byte[8] { 0xF4, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4F };
            string path = MainWindow.PathText;

            UpdateStatusInfo("准备升级...", "Blue");

            int temp;
            int receivedTimeoutCount = 0;

            try
            {
                FileStream fileStream = new FileStream(@path, FileMode.Open, FileAccess.Read);

                Dtimer.Start();
                UpdateStatusInfo("等待设备响应...", "Blue");

                // 阶段1: 等待设备发送'C'
                while (FUCK_C == 0)
                {
                    n = MainWindow.serialPort.BytesToRead;
                    if (n > 0)
                    {
                        byte[] buf = new byte[n];
                        MainWindow.serialPort.Read(buf, 0, n);

                        UpdateStatusInfo($"收到响应: {buf[0]:X2}", "Green");

                        if (buf[0] != C)
                        {
                            UpdateStatusInfo("发送启动命令...", "Orange");
                            MainWindow.serialPort.Write(sendData, 0, sendData.Length);
                            MainWindow.serialPort.DiscardInBuffer();
                        }
                        else
                        {
                            FUCK_C = 1;
                            Dtimer.Stop();
                            UpdateStatusInfo("设备已就绪，开始发送文件头", "Green");
                        }
                    }
                    else
                    {
                        UpdateStatusInfo("发送启动命令...", "Blue");
                        MainWindow.serialPort.Write(sendData, 0, sendData.Length);
                    }
                    Thread.Sleep(300);
                }

                Thread.Sleep(100);

                // 阶段2: 发送文件头包
                UpdateStatusInfo("发送文件头信息...", "Blue");
                sendYmodemInitialPacket(STX, packetNumber, invertedPacketNumber, databin, dataSize, path, fileStream, CRC, crcSize);

                const int timeout = 3000;
                var startTimeWait = Environment.TickCount;
                bool receivedAck = false;

                // 等待ACK
                while (Environment.TickCount - startTimeWait < timeout)
                {
                    if (MainWindow.serialPort.BytesToRead > 0)
                    {
                        var ReadByte = MainWindow.serialPort.ReadByte();
                        if (ReadByte == 0x06)
                        {
                            receivedAck = true;
                            UpdateStatusInfo("文件头确认成功", "Green");
                            break;
                        }
                        else if (ReadByte == 0x15)
                        {
                            receivedAck = false;
                            UpdateStatusInfo("文件头确认失败，重新发送文件头", "Orange");
                            break;
                        }
                        else if (ReadByte == 0x18)
                        {
                            UpdateStatusInfo($"收到CA，升级失败", "Red");
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                CloseButtonEnabled = true;
                                if (thread1 != null && thread1.IsAlive)
                                    thread1.Abort();
                            }), null);
                            return;
                        }
                    }
                    Thread.Sleep(50);
                }

                if (receivedAck == false)
                {
                    sendYmodemInitialPacket(STX, packetNumber, invertedPacketNumber, databin, dataSize, path, fileStream, CRC, crcSize);

                    startTimeWait = Environment.TickCount;
                    // 等待ACK
                    while (Environment.TickCount - startTimeWait < timeout)
                    {
                        if (MainWindow.serialPort.BytesToRead > 0)
                        {
                            if (MainWindow.serialPort.ReadByte() == 0x06)
                            {
                                receivedAck = true;
                                UpdateStatusInfo("文件头确认成功", "Green");
                                break;
                            }
                            else
                            {
                                receivedAck = false;
                                UpdateStatusInfo("文件头确认失败", "Red");
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    CloseButtonEnabled = true;
                                    if (thread1 != null && thread1.IsAlive)
                                        thread1.Abort();
                                }), null);
                                return;
                            }
                        }
                    }
                }

                Thread.Sleep(100);

                // 阶段3: 发送数据包
                UpdateStatusInfo("开始发送数据包...", "Blue");
                receivedTimeoutCount = 0;
                int fileReadCount = 0;
                int totalPacketsSent = 0;

                do
                {
                    bool responseReceived = false;
                    if (receivedAck == false)
                    {
                        if (receivedTimeoutCount < 5)
                        {
                            UpdateStatusInfo($"重试包 {packetNumber}", "Orange");

                            sendYmodemPacket(STX, packetNumber, invertedPacketNumber, databin, dataSize, CRC, crcSize);

                            Thread.Sleep(300);

                            receivedTimeoutCount++;
                        }
                        else
                        {
                            receivedTimeoutCount = 0;
                            UpdateStatusInfo("重试次数过多，升级失败", "Red");
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                CloseButtonEnabled = true;
                                if (thread1 != null && thread1.IsAlive)
                                    thread1.Abort();
                            }), null);
                            return;
                        }
                    }
                    else
                    {
                        // 读取文件数据
                        fileReadCount = fileStream.Read(databin, 0, dataSize);
                        if (fileReadCount == 0) break;

                        if (fileReadCount != dataSize)
                            for (int i = fileReadCount; i < dataSize; i++)
                                databin[i] = 0;

                        packetNumber++;
                        totalPacketsSent++;

                        invertedPacketNumber = 255 - packetNumber;

                        // 计算CRC
                        Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
                        CRC = crc16Ccitt.ComputeChecksumBytes(databin);

                        // 发送数据包
                        Thread.Sleep(10);
                        UpdateStatusInfo($"发送包 {totalPacketsSent}/{fileSizenum}", "Blue");
                        sendYmodemPacket(STX, packetNumber, invertedPacketNumber, databin, dataSize, CRC, crcSize);
                        UpdatePacketInfo(totalPacketsSent, fileSizenum);
                    }

                    var startTimeTick = Environment.TickCount;

                    // 等待ACK
                    while (Environment.TickCount - startTimeTick < timeout)
                    {
                        if (MainWindow.serialPort.BytesToRead > 0)
                        {
                            temp = MainWindow.serialPort.ReadByte();
                            responseReceived = true;
                            if (temp == ACK)
                            {
                                receivedAck = true;
                                receivedTimeoutCount = 0;
                                break;
                            }
                            if (temp == NAK)
                            {
                                receivedAck = false;
                                UpdateStatusInfo($"收到NAK，准备重试", "Orange");
                                break;
                            }
                            else if (temp == CA)
                            {
                                UpdateStatusInfo($"收到CA，升级失败", "Red");
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    CloseButtonEnabled = true;
                                    if (thread1 != null && thread1.IsAlive)
                                        thread1.Abort();
                                }), null);
                                return;
                            }
                        }
                    }

                    if (!responseReceived)
                    {
                        UpdateStatusInfo("等待ACK超时", "Red");
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            CloseButtonEnabled = true;
                            if (thread1 != null && thread1.IsAlive)
                                thread1.Abort();
                        }), null);
                        return;
                    }
                } while (dataSize == fileReadCount);

                Thread.Sleep(100);

                // 阶段4: 发送EOT结束
                UpdateStatusInfo("发送传输结束标志...", "Blue");
                MainWindow.serialPort.Write(new byte[] { EOT }, 0, 1);

                packetNumber = 0;
                invertedPacketNumber = 255;
                databin = new byte[dataSize];
                CRC = new byte[crcSize];

                sendYmodemClosingPacket(STX, packetNumber, invertedPacketNumber, databin, dataSize, CRC, crcSize);

                startTimeWait = Environment.TickCount;
                // 等待ACK
                while (Environment.TickCount - startTimeWait < timeout)
                {
                    if (MainWindow.serialPort.BytesToRead > 0)
                    {
                        if (MainWindow.serialPort.ReadByte() == 0x06)
                        {
                            UpdatePacketInfo(fileSizenum, fileSizenum);
                            UpdateStatusInfo("固件升级完成", "Green");
                            break;
                        }
                        else if (MainWindow.serialPort.ReadByte() == 0x15)
                        {
                            UpdateStatusInfo("固件完整性校验失败，请重新烧录", "Red");
                            break;
                        }
                    }
                    Thread.Sleep(10);
                }

                // 更新最终进度
                UpdatePacketInfo(fileSizenum, fileSizenum);

                fileStream.Close();

                // 停止定时器
                statusTimer.Stop();
                Dtimer.Stop();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CloseButtonEnabled = true;
                    if (thread1 != null && thread1.IsAlive)
                        thread1.Abort();
                }), null);

            }
            catch (Exception ex)
            {
                UpdateStatusInfo($"升级异常: {ex.Message}\n请检查硬件和接线", "Red");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    CloseButtonEnabled = true;
                    if (thread1 != null && thread1.IsAlive)
                        thread1.Abort();
                }), null);
            }
        }

        private void sendYmodemPacket(byte STX, int packetNumber, int invertedPacketNumber, byte[] databin, int dataSize, byte[] CRC, int crcSize)
        {
            MainWindow.serialPort.Write(new byte[] { STX }, 0, 1);
            MainWindow.serialPort.Write(new byte[] { (byte)packetNumber }, 0, 1);
            MainWindow.serialPort.Write(new byte[] { (byte)invertedPacketNumber }, 0, 1);
            MainWindow.serialPort.Write(databin, 0, dataSize);
            MainWindow.serialPort.Write(CRC, 0, crcSize);
        }

        private void sendYmodemInitialPacket(byte STX, int packetNumber, int invertedPacketNumber, byte[] databin, int dataSize, string path, FileStream fileStream, byte[] CRC, int crcSize)
        {
            string fileName = System.IO.Path.GetFileName(path);
            string fileSize = fileStream.Length.ToString();
            int filenum = int.Parse(fileSize);
            fileSizenum = (filenum / 1024) + 2;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                progressBar1.Maximum = fileSizenum;
                txtProgressPercent.Text = "0%";
                progressBar1.Value = 0;
            }), null);

            int i;
            for (i = 0; i < fileName.Length && (fileName.ToCharArray()[i] != 0); i++)
            {
                databin[i] = (byte)fileName.ToCharArray()[i];
            }
            databin[i] = 0;

            int j;
            for (j = 0; j < fileSize.Length && (fileSize.ToCharArray()[j] != 0); j++)
            {
                databin[(i + 1) + j] = (byte)fileSize.ToCharArray()[j];
            }
            databin[(i + 1) + j] = 0;

            for (int k = ((i + 1) + j) + 1; k < dataSize; k++)
            {
                databin[k] = 0;
            }

            Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            CRC = crc16Ccitt.ComputeChecksumBytes(databin);

            sendYmodemPacket(STX, packetNumber, invertedPacketNumber, databin, dataSize, CRC, crcSize);
        }

        private void sendYmodemClosingPacket(byte STX, int packetNumber, int invertedPacketNumber, byte[] databin, int dataSize, byte[] CRC, int crcSize)
        {
            Crc16Ccitt crc16Ccitt = new Crc16Ccitt(InitialCrcValue.Zeros);
            CRC = crc16Ccitt.ComputeChecksumBytes(databin);

            sendYmodemPacket(STX, packetNumber, invertedPacketNumber, databin, dataSize, CRC, crcSize);
        }

        public class Crc16Ccitt
        {
            const ushort poly = 0x1021;
            ushort[] table = new ushort[256];
            ushort initialValue = 0;

            public ushort ComputeChecksum(byte[] bytes)
            {
                ushort crc = this.initialValue;
                for (int i = 0; i < bytes.Length; ++i)
                {
                    crc = (ushort)((crc << 8) ^ table[((crc >> 8) ^ (0xff & bytes[i]))]);
                }
                return crc;
            }

            public byte[] ComputeChecksumBytes(byte[] bytes)
            {
                ushort crc = ComputeChecksum(bytes);
                return new byte[] { (byte)(crc >> 8), (byte)(crc & 0x00ff) };
            }

            public Crc16Ccitt(InitialCrcValue initialValue)
            {
                this.initialValue = (ushort)initialValue;
                ushort temp, a;
                for (int i = 0; i < table.Length; ++i)
                {
                    temp = 0;
                    a = (ushort)(i << 8);
                    for (int j = 0; j < 8; ++j)
                    {
                        if (((temp ^ a) & 0x8000) != 0)
                        {
                            temp = (ushort)((temp << 1) ^ poly);
                        }
                        else
                        {
                            temp <<= 1;
                        }
                        a <<= 1;
                    }
                    table[i] = temp;
                }
            }
        }
    }
}