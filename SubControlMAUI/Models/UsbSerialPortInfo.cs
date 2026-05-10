
using System.Text.Json.Serialization;


public record UsbSerialPortInfo
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = "";

    [JsonPropertyName("vendorId")]
    public string VendorId { get; init; } = "";

    [JsonPropertyName("productId")]
    public string ProductId { get; init; } = "";

    [JsonPropertyName("serialNumber")]
    public string SerialNumber { get; init; } = "";

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("portPath")]
    public string PortPath { get; init; } = "";
}

public record ListDevicesResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("data")]
    public List<UsbSerialPortInfo> Data { get; init; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}