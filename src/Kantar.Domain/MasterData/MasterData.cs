namespace Kantar.Domain.MasterData;

public sealed class Product
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public required string Unit { get; set; }
    public decimal ListPrice { get; set; }
    public int ReorderLevel { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string TaxNumber { get; set; }
    public required string Address { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Supplier
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Warehouse
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
