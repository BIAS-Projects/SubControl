namespace SubConsole.Models;

public sealed class GpioUartOptions
{
    public const string SectionName = "GpioUart";

    public bool Enabled { get; set; } = false;
    public string PortPath { get; set; } = "/dev/ttyAMA0";
    public string FunctionName { get; set; } = "GPIO_UART0";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public string Handshake { get; set; } = "None";
    public bool AutoOpen { get; set; } = true;
}