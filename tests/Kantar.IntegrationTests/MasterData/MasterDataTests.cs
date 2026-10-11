using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.MasterData;

/// <summary>docs/02 §3 and docs/05 §4: master data is managed by the administrator, versioned with ETags and never deleted.</summary>
public sealed class MasterDataTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private Task<ApiClient> AdminAsync() => _world.LoginAsAsync([Roles.SystemAdmin]);

    /// <summary>Codes are 2–16 characters for warehouses, so the shortest unique form is used everywhere.</summary>
    private static string Code(string prefix) => $"{prefix}{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";

    private static async Task<(JsonNode Body, string ETag)> CreateAsync(ApiClient admin, string url, object body)
    {
        using var response = await admin.PostAsync(url, body, ApiClient.NewKey());
        var json = await response.JsonAsync(HttpStatusCode.Created);
        Assert.Equal($"{url}/{json.Id()}", response.Headers.Location?.ToString());
        Assert.Equal(json.Str("eTag"), response.ETag());
        return (json, response.ETag());
    }

    [Fact]
    public async Task Product_is_created_read_updated_with_if_match_and_switched_off_and_on()
    {
        var admin = await AdminAsync();
        var sku = Code("PRD-");

        var (created, etag) = await CreateAsync(admin, "/api/products", new { sku = "  " + sku.ToLowerInvariant() + " ", name = "  Çelik Vida M8  ", unit = "koli", listPrice = 12.50m, reorderLevel = 40 });
        Assert.Equal((sku, "Çelik Vida M8", "KOLI", 12.50m, 40, true),
            (created.Str("sku"), created.Str("name"), created.Str("unit"), created["listPrice"]!.GetValue<decimal>(), created.Number("reorderLevel"), created["isActive"]!.GetValue<bool>()));
        var url = $"/api/products/{created.Id()}";

        using var read = await admin.GetAsync(url);
        Assert.Equal(sku, (await read.JsonAsync(HttpStatusCode.OK)).Str("sku"));
        Assert.Equal(etag, read.ETag());
        var listed = await admin.GetJsonAsync($"/api/products?search={sku}&active=true");
        Assert.Equal([created.Id()], listed["items"]!.AsArray().Select(p => p!.Id()));

        var update = new { sku, name = "Çelik Vida M8 (galvaniz)", unit = "KOLI", listPrice = 13.75m, reorderLevel = 60 };
        using var withoutIfMatch = await admin.PutAsync(url, update);
        await withoutIfMatch.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        using var updated = await admin.PutAsync(url, update, etag);
        var second = await updated.JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("Çelik Vida M8 (galvaniz)", 13.75m, 60), (second.Str("name"), second["listPrice"]!.GetValue<decimal>(), second.Number("reorderLevel")));
        Assert.NotEqual(etag, updated.ETag());

        // A second editor still holding the first version does not overwrite the first editor's change.
        using var stale = await admin.PutAsync(url, new { sku, name = "Eski sürümden yazılan ad", unit = "KOLI", listPrice = 1m, reorderLevel = 0 }, etag);
        await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        Assert.Equal("Çelik Vida M8 (galvaniz)", (await admin.GetJsonAsync(url)).Str("name"));

        using var off = await admin.PostAsync(url + "/deactivate");
        Assert.False((await off.JsonAsync(HttpStatusCode.OK))["isActive"]!.GetValue<bool>());
        Assert.Empty((await admin.GetJsonAsync($"/api/products?search={sku}&active=true"))["items"]!.AsArray());
        Assert.Single((await admin.GetJsonAsync($"/api/products?search={sku}&active=false"))["items"]!.AsArray());
        using var on = await admin.PostAsync(url + "/activate");
        Assert.True((await on.JsonAsync(HttpStatusCode.OK))["isActive"]!.GetValue<bool>());

        using var missing = await admin.GetAsync($"/api/products/{Guid.NewGuid()}");
        await missing.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Equal(1, await _world.CountAsync("SELECT COUNT(*) FROM md.Products WHERE Sku = @s;", new SqlParameter("@s", sku)));
        // Created, updated, deactivated, activated: four audit rows; the refused writes left none.
        Assert.Equal(4, await _world.CountAsync(
            "SELECT COUNT(*) FROM ops.AuditEntries WHERE EntityId = @id AND Action LIKE 'masterdata.product[_]%';", new SqlParameter("@id", created.Id())));
    }

    [Fact]
    public async Task Two_concurrent_product_updates_with_the_same_etag_let_exactly_one_through()
    {
        var admin = await AdminAsync();
        var clients = _world.Clones(admin, 2);

        for (var round = 0; round < 5; round++)
        {
            var sku = Code("PRD-");
            var (created, etag) = await CreateAsync(admin, "/api/products", new { sku, name = "İlk ad", unit = "ADET", listPrice = 10m, reorderLevel = 0 });
            var url = $"/api/products/{created.Id()}";
            var prices = new[] { 11m, 12m };

            var replies = await Race.RunAsync(prices.Select((price, i) => (Func<Task<HttpResponseMessage>>)(() =>
                clients[i].PutAsync(url, new { sku, name = $"Yazar {i}", unit = "ADET", listPrice = price, reorderLevel = 0 }, etag))));

            // No silent lost update: one editor wins, the other is told the row changed under them.
            var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
            Assert.Single(replies, r => r != winner).AssertProblem(HttpStatusCode.PreconditionFailed, "precondition_failed");
            var stored = await admin.GetJsonAsync(url);
            Assert.Equal((winner.Json.Str("name"), winner.Json["listPrice"]!.GetValue<decimal>()), (stored.Str("name"), stored["listPrice"]!.GetValue<decimal>()));
            Assert.Equal(prices[Array.IndexOf(replies, winner)], stored["listPrice"]!.GetValue<decimal>());
        }
    }

    [Fact]
    public async Task Product_input_is_validated()
    {
        var admin = await AdminAsync();
        var sku = Code("PRD-");
        var invalid = new (string Field, object Body)[]
        {
            ("sku", new { sku = "AB", name = "Ürün", unit = "ADET", listPrice = 1m, reorderLevel = 0 }),
            ("sku", new { sku = "ÇAY 1", name = "Ürün", unit = "ADET", listPrice = 1m, reorderLevel = 0 }),
            ("sku", new { sku = new string('A', 33), name = "Ürün", unit = "ADET", listPrice = 1m, reorderLevel = 0 }),
            ("name", new { sku, name = "Ü", unit = "ADET", listPrice = 1m, reorderLevel = 0 }),
            ("unit", new { sku, name = "Ürün", unit = "DÜZİNE", listPrice = 1m, reorderLevel = 0 }),
            ("listPrice", new { sku, name = "Ürün", unit = "ADET", listPrice = -0.01m, reorderLevel = 0 }),
            ("listPrice", new { sku, name = "Ürün", unit = "ADET", listPrice = 1.005m, reorderLevel = 0 }),
            ("reorderLevel", new { sku, name = "Ürün", unit = "ADET", listPrice = 1m, reorderLevel = -1 }),
        };

        foreach (var (field, body) in invalid)
        {
            using var response = await admin.PostAsync("/api/products", body, ApiClient.NewKey());
            var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
            Assert.Contains(field, problem["errors"]!.AsArray().Select(e => e!.Str("field")));
        }

        using var asString = await admin.SendAsync(HttpMethod.Post, "/api/products", idempotencyKey: ApiClient.NewKey(),
            rawBody: $$"""{"sku":"{{sku}}","name":"Ürün","unit":"ADET","listPrice":"12.50","reorderLevel":0}""");
        Assert.Equal(HttpStatusCode.BadRequest, asString.StatusCode);
        using var withoutKey = await admin.PostAsync("/api/products", new { sku, name = "Ürün", unit = "ADET", listPrice = 1m, reorderLevel = 0 });
        await withoutKey.ProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        Assert.Equal(0, await _world.CountAsync("SELECT COUNT(*) FROM md.Products WHERE Sku = @s;", new SqlParameter("@s", sku)));
    }

    [Fact]
    public async Task Customer_supplier_and_warehouse_follow_the_same_pattern()
    {
        var admin = await AdminAsync();
        var resources = new (string Url, object Create, object Update, string Changed)[]
        {
            ("/api/customers",
                new { code = Code("CUS-"), name = "Örnek Ticaret A.Ş.", taxNumber = "1234567890", address = "Sentetik Mah. No:1\nİstanbul" },
                new { code = "?", name = "Örnek Ticaret ve Sanayi A.Ş.", taxNumber = "12345678901", address = "Yeni Mah. No:2 Ankara" },
                "Örnek Ticaret ve Sanayi A.Ş."),
            ("/api/suppliers", new { code = Code("SUP-"), name = "Örnek Tedarik Ltd." }, new { code = "?", name = "Örnek Tedarik ve Lojistik Ltd." }, "Örnek Tedarik ve Lojistik Ltd."),
            ("/api/warehouses", new { code = Code("WH"), name = "Gebze Deposu" }, new { code = "?", name = "Gebze Ana Depo" }, "Gebze Ana Depo"),
        };

        foreach (var (collection, create, updateTemplate, changedName) in resources)
        {
            var (created, etag) = await CreateAsync(admin, collection, create);
            var code = created.Str("code");
            var url = $"{collection}/{created.Id()}";
            Assert.True(created["isActive"]!.GetValue<bool>());

            using var read = await admin.GetAsync(url);
            Assert.Equal(code, (await read.JsonAsync(HttpStatusCode.OK)).Str("code"));
            Assert.Equal(etag, read.ETag());
            using var listedResponse = await admin.GetAsync(collection);
            var listed = (await listedResponse.JsonAsync(HttpStatusCode.OK)).AsArray();
            Assert.Contains(created.Id(), listed.Select(r => r!.Id()));

            // The update body is the create body with the name (and for a customer the other free fields) changed.
            var update = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(updateTemplate))!.AsObject();
            update["code"] = code;
            using var withoutIfMatch = await admin.SendAsync(HttpMethod.Put, url, rawBody: update.ToJsonString());
            Assert.True(withoutIfMatch.StatusCode == HttpStatusCode.PreconditionRequired, $"PUT {collection} without If-Match: {(int)withoutIfMatch.StatusCode}");
            using var updated = await admin.SendAsync(HttpMethod.Put, url, ifMatch: etag, rawBody: update.ToJsonString());
            Assert.Equal(changedName, (await updated.JsonAsync(HttpStatusCode.OK)).Str("name"));
            Assert.NotEqual(etag, updated.ETag());
            using var stale = await admin.SendAsync(HttpMethod.Put, url, ifMatch: etag, rawBody: update.ToJsonString());
            await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");

            // The code identifies the row in documents and is not editable.
            update["code"] = Code("X");
            using var recode = await admin.SendAsync(HttpMethod.Put, url, ifMatch: updated.ETag(), rawBody: update.ToJsonString());
            await recode.ProblemAsync(HttpStatusCode.Conflict, "immutable_field");

            using var off = await admin.PostAsync(url + "/deactivate");
            Assert.False((await off.JsonAsync(HttpStatusCode.OK))["isActive"]!.GetValue<bool>());
            using var on = await admin.PostAsync(url + "/activate");
            var final = await on.JsonAsync(HttpStatusCode.OK);
            Assert.Equal((code, changedName, true), (final.Str("code"), final.Str("name"), final["isActive"]!.GetValue<bool>()));
            using var missing = await admin.GetAsync($"{collection}/{Guid.NewGuid()}");
            await missing.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }
    }

    [Fact]
    public async Task A_code_that_is_already_taken_is_a_conflict_in_any_spelling()
    {
        var admin = await AdminAsync();
        var bodies = new (string Url, Func<string, object> Body)[]
        {
            ("/api/products", code => new { sku = code, name = "Ürün", unit = "ADET", listPrice = 1m, reorderLevel = 0 }),
            ("/api/customers", code => new { code, name = "Müşteri", taxNumber = "1234567890", address = "Adres satırı" }),
            ("/api/suppliers", code => new { code, name = "Tedarikçi" }),
            ("/api/warehouses", code => new { code, name = "Depo" }),
        };

        foreach (var (url, body) in bodies)
        {
            var code = Code("DUP");
            var key = ApiClient.NewKey();
            var first = await Reply.ReadAsync(admin.PostAsync(url, body(code), key));
            Assert.Equal(HttpStatusCode.Created, first.Status);

            foreach (var spelling in new[] { code, code.ToLowerInvariant(), "  " + code + "  " })
            {
                var duplicate = await Reply.ReadAsync(admin.PostAsync(url, body(spelling), ApiClient.NewKey()));
                Assert.True(duplicate.Status == HttpStatusCode.Conflict, $"{url} '{spelling}': {(int)duplicate.Status}");
                Assert.Equal("code_taken", duplicate.Problem);
            }

            // The original request repeated with its own key is a replay, not a conflict.
            var replay = await Reply.ReadAsync(admin.PostAsync(url, body(code), key));
            Assert.Equal((HttpStatusCode.Created, true), (replay.Status, replay.Replayed));
            Assert.Equal(first.Body, replay.Body);
        }

        // Ten administrators' worth of the same new SKU at once: one row.
        var sku = Code("RACE-");
        var replies = await Race.RunAsync(_world.Clones(admin, 10).Select(client => (Func<Task<HttpResponseMessage>>)(() =>
            client.PostAsync("/api/products", new { sku, name = "Yarış Ürünü", unit = "ADET", listPrice = 1m, reorderLevel = 0 }, ApiClient.NewKey()))));
        Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        Assert.All(replies.Where(r => r.Status != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.Status));
        Assert.Equal(1, await _world.CountAsync("SELECT COUNT(*) FROM md.Products WHERE Sku = @s;", new SqlParameter("@s", sku)));
    }

    [Fact]
    public async Task Sku_and_unit_are_frozen_once_a_product_has_a_document_line_or_a_movement()
    {
        var admin = await AdminAsync();
        var warehouse = await _world.WarehouseAsync();
        var supplier = await _world.SupplierAsync();
        var (unused, onDraft, moved) = (await _world.ProductAsync(), await _world.ProductAsync(), await _world.ProductAsync());
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);

        // One product only appears on a draft purchase order line; another only has a stock movement.
        await World.CreatePurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (onDraft.Id, 3));
        using var adjusted = await World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Found", (moved.Id, 2));
        await adjusted.JsonAsync(HttpStatusCode.Created);

        foreach (var product in new[] { onDraft, moved })
        {
            var url = $"/api/products/{product.Id}";
            var etag = await World.ETagAsync(admin, url);
            using var newSku = await admin.PutAsync(url, new { sku = Code("NEW-"), name = product.Name, unit = "ADET", listPrice = 100m, reorderLevel = 0 }, etag);
            await newSku.ProblemAsync(HttpStatusCode.Conflict, "immutable_field");
            using var newUnit = await admin.PutAsync(url, new { sku = product.Sku, name = product.Name, unit = "KOLI", listPrice = 100m, reorderLevel = 0 }, etag);
            await newUnit.ProblemAsync(HttpStatusCode.Conflict, "immutable_field");

            // Name, price and reorder level stay editable.
            using var rest = await admin.PutAsync(url, new { sku = product.Sku, name = "Yeni ad", unit = "ADET", listPrice = 150m, reorderLevel = 7 }, etag);
            var updated = await rest.JsonAsync(HttpStatusCode.OK);
            Assert.Equal((product.Sku, "ADET", "Yeni ad", 150m), (updated.Str("sku"), updated.Str("unit"), updated.Str("name"), updated["listPrice"]!.GetValue<decimal>()));
        }

        // A product nothing refers to yet can still be re-coded, but not onto a code that is in use.
        var unusedUrl = $"/api/products/{unused.Id}";
        var unusedETag = await World.ETagAsync(admin, unusedUrl);
        using var taken = await admin.PutAsync(unusedUrl, new { sku = moved.Sku, name = unused.Name, unit = "ADET", listPrice = 100m, reorderLevel = 0 }, unusedETag);
        await taken.ProblemAsync(HttpStatusCode.Conflict, "code_taken");
        var recoded = Code("NEW-");
        using var free = await admin.PutAsync(unusedUrl, new { sku = recoded, name = unused.Name, unit = "PAKET", listPrice = 100m, reorderLevel = 0 }, unusedETag);
        var result = await free.JsonAsync(HttpStatusCode.OK);
        Assert.Equal((recoded, "PAKET"), (result.Str("sku"), result.Str("unit")));
        await _world.AssertReconciledAsync(warehouse.Id, moved.Id);
    }

    [Fact]
    public async Task Only_the_system_admin_writes_master_data()
    {
        var admin = await AdminAsync();
        var (product, _) = await CreateAsync(admin, "/api/products", new { sku = Code("PRD-"), name = "Ürün", unit = "ADET", listPrice = 10m, reorderLevel = 0 });
        var (customer, _) = await CreateAsync(admin, "/api/customers", new { code = Code("CUS-"), name = "Müşteri", taxNumber = "1234567890", address = "Adres satırı" });
        var (supplier, _) = await CreateAsync(admin, "/api/suppliers", new { code = Code("SUP-"), name = "Tedarikçi" });
        var (warehouse, _) = await CreateAsync(admin, "/api/warehouses", new { code = Code("WH"), name = "Depo" });
        var anyETag = "\"AAAAAAAAB9E=\"";

        foreach (var role in Roles.All.Where(r => r != Roles.SystemAdmin))
        {
            var client = await _world.LoginAsAsync([role], [(warehouse.Id(), WarehouseLevel.Supervise)], poLimit: 1_000_000m);
            var attempts = new (string Name, Func<Task<HttpResponseMessage>> Send)[]
            {
                ("create product", () => client.PostAsync("/api/products", new { sku = Code("PRD-"), name = "Ürün", unit = "ADET", listPrice = 1m, reorderLevel = 0 }, ApiClient.NewKey())),
                ("create customer", () => client.PostAsync("/api/customers", new { code = Code("CUS-"), name = "Müşteri", taxNumber = "1234567890", address = "Adres satırı" }, ApiClient.NewKey())),
                ("create supplier", () => client.PostAsync("/api/suppliers", new { code = Code("SUP-"), name = "Tedarikçi" }, ApiClient.NewKey())),
                ("create warehouse", () => client.PostAsync("/api/warehouses", new { code = Code("WH"), name = "Depo" }, ApiClient.NewKey())),
                ("update product", () => client.PutAsync($"/api/products/{product.Id()}", new { sku = product.Str("sku"), name = "Ele geçirildi", unit = "ADET", listPrice = 0.01m, reorderLevel = 0 }, anyETag)),
                ("update customer", () => client.PutAsync($"/api/customers/{customer.Id()}", new { code = customer.Str("code"), name = "Ele geçirildi", taxNumber = "1234567890", address = "Adres satırı" }, anyETag)),
                ("update supplier", () => client.PutAsync($"/api/suppliers/{supplier.Id()}", new { code = supplier.Str("code"), name = "Ele geçirildi" }, anyETag)),
                ("update warehouse", () => client.PutAsync($"/api/warehouses/{warehouse.Id()}", new { code = warehouse.Str("code"), name = "Ele geçirildi" }, anyETag)),
                ("deactivate product", () => client.PostAsync($"/api/products/{product.Id()}/deactivate")),
                ("deactivate customer", () => client.PostAsync($"/api/customers/{customer.Id()}/deactivate")),
                ("deactivate supplier", () => client.PostAsync($"/api/suppliers/{supplier.Id()}/deactivate")),
                ("deactivate warehouse", () => client.PostAsync($"/api/warehouses/{warehouse.Id()}/deactivate")),
                ("activate product", () => client.PostAsync($"/api/products/{product.Id()}/activate")),
            };

            foreach (var (name, send) in attempts)
            {
                var reply = await Reply.ReadAsync(send());
                Assert.True(reply.Status == HttpStatusCode.Forbidden, $"{role} / {name}: {(int)reply.Status} {reply.Text}");
                Assert.Equal("forbidden", reply.Problem);
            }
        }

        // Nothing changed.
        var after = await admin.GetJsonAsync($"/api/products/{product.Id()}");
        Assert.Equal(("Ürün", 10m, true), (after.Str("name"), after["listPrice"]!.GetValue<decimal>(), after["isActive"]!.GetValue<bool>()));
        Assert.Equal(("Müşteri", true), ((await admin.GetJsonAsync($"/api/customers/{customer.Id()}")).Str("name"), (await admin.GetJsonAsync($"/api/customers/{customer.Id()}"))["isActive"]!.GetValue<bool>()));
        Assert.Equal(("Tedarikçi", true), ((await admin.GetJsonAsync($"/api/suppliers/{supplier.Id()}")).Str("name"), (await admin.GetJsonAsync($"/api/suppliers/{supplier.Id()}"))["isActive"]!.GetValue<bool>()));
        Assert.Equal(("Depo", true), ((await admin.GetJsonAsync($"/api/warehouses/{warehouse.Id()}")).Str("name"), (await admin.GetJsonAsync($"/api/warehouses/{warehouse.Id()}"))["isActive"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Passive_master_data_blocks_new_sales_drafts_and_confirmation_until_it_is_active_again()
    {
        var admin = await AdminAsync();
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 20);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (draft, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 4));
        var body = new { customerId = customer.Id, warehouseId = warehouse.Id, lines = new[] { new { productId = product.Id, quantity = 1 } } };

        foreach (var url in new[] { $"/api/customers/{customer.Id}", $"/api/warehouses/{warehouse.Id}", $"/api/products/{product.Id}" })
        {
            using var off = await admin.PostAsync(url + "/deactivate");
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);

            using var create = await sales.PostAsync("/api/sales/orders", body, ApiClient.NewKey());
            await create.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
            using var edit = await sales.PutAsync($"/api/sales/orders/{draft.Id()}", body, etag);
            await edit.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
            using var confirm = await sales.PostAsync($"/api/sales/orders/{draft.Id()}/confirm", null, ApiClient.NewKey(), etag);
            await confirm.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
            Assert.Equal((20, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

            using var on = await admin.PostAsync(url + "/activate");
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        }

        // The draft was never touched by the refusals: its first version still confirms.
        using var stored = await sales.GetAsync($"/api/sales/orders/{draft.Id()}");
        Assert.Equal(etag, stored.ETag());
        var (confirmed, _) = await World.ConfirmAsync(sales, draft, etag);
        Assert.Equal("Confirmed", confirmed.Str("status"));
        Assert.Equal((20, 4), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.CountAsync("SELECT COUNT(*) FROM sales.SalesOrders WHERE WarehouseId = @w;", new SqlParameter("@w", warehouse.Id)));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Passive_master_data_blocks_new_purchase_drafts_and_approval_until_it_is_active_again()
    {
        var admin = await AdminAsync();
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var supplier = await _world.SupplierAsync();
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)], poLimit: 1_000_000m);
        var (draft, etag) = await World.CreatePurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 4));
        var body = new { supplierId = supplier.Id, warehouseId = warehouse.Id, lines = new[] { new { productId = product.Id, quantity = 1, unitCost = 5.00m } } };

        foreach (var url in new[] { $"/api/suppliers/{supplier.Id}", $"/api/warehouses/{warehouse.Id}", $"/api/products/{product.Id}" })
        {
            using var off = await admin.PostAsync(url + "/deactivate");
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);

            using var create = await buyer.PostAsync("/api/purchasing/orders", body, ApiClient.NewKey());
            await create.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
            using var edit = await buyer.PutAsync($"/api/purchasing/orders/{draft.Id()}", body, etag);
            await edit.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
            using var approve = await buyer.PostAsync($"/api/purchasing/orders/{draft.Id()}/approve", null, ApiClient.NewKey(), etag);
            await approve.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");

            using var on = await admin.PostAsync(url + "/activate");
            Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        }

        using var approved = await buyer.PostAsync($"/api/purchasing/orders/{draft.Id()}/approve", null, ApiClient.NewKey(), etag);
        Assert.Equal("Approved", (await approved.JsonAsync(HttpStatusCode.OK)).Str("status"));

        // An approved order is a commitment: it is still received after its product and supplier go passive.
        using var productOff = await admin.PostAsync($"/api/products/{product.Id}/deactivate");
        using var supplierOff = await admin.PostAsync($"/api/suppliers/{supplier.Id}/deactivate");
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (productOff.StatusCode, supplierOff.StatusCode));
        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        using var received = await World.ReceiveAsync(operatorClient, draft.Id(), _world.Reference("IRS"), (draft["lines"]![0]!.Id(), 4));
        Assert.Equal("Received", (await received.JsonAsync(HttpStatusCode.Created)).Str("purchaseOrderStatus"));
        Assert.Equal((4, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }
}
