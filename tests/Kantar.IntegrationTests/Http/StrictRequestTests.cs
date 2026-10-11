using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Http;

/// <summary>docs/05 §1.1 and §1.4: the request contract is closed, and every refusal is a problem document without internals.</summary>
public sealed class StrictRequestTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private sealed record Desk(ApiClient Client, User User, Warehouse Warehouse, Product Product, Customer Customer)
    {
        /// <summary>A sales order body with one member replaced or added, written by hand so the test controls every byte.</summary>
        public string Body(string lineExtra = "", string extra = "", string quantity = "1") =>
            $$"""{"customerId":"{{Customer.Id}}","warehouseId":"{{Warehouse.Id}}","lines":[{"productId":"{{Product.Id}}","quantity":{{quantity}}{{lineExtra}}}]{{extra}}}""";
    }

    private async Task<Desk> DeskAsync()
    {
        var warehouse = await _world.WarehouseAsync();
        var user = await _world.UserAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        return new Desk(await _world.LoginAsync(user), user, warehouse, await _world.ProductAsync(), await _world.CustomerAsync());
    }

    private static Task<Reply> PostOrderAsync(Desk desk, string json, string contentType = "application/json") =>
        Reply.ReadAsync(desk.Client.SendAsync(HttpMethod.Post, "/api/sales/orders", idempotencyKey: ApiClient.NewKey(), rawBody: json, contentType: contentType));

    private Task<int> OrderCountAsync(Desk desk) =>
        _world.CountAsync("SELECT COUNT(*) FROM sales.SalesOrders WHERE OwnerUserId = @u;", new SqlParameter("@u", desk.User.Id));

    /// <summary>The shape every refusal must have, and the things it must never contain.</summary>
    private static JsonNode AssertCleanProblem(Reply reply, HttpStatusCode status, string code)
    {
        Assert.True(reply.Status == status, $"Expected {(int)status} {code} but got {(int)reply.Status}: {reply.Text}");
        Assert.Equal("application/problem+json; charset=utf-8", reply.ContentType);
        var problem = reply.Json;
        Assert.Equal(code, problem.Str("code"));
        Assert.Equal((int)status, problem.Number("status"));
        Assert.Equal($"https://kantar.invalid/problems/{code}", problem.Str("type"));
        Assert.False(string.IsNullOrWhiteSpace(problem.Str("title")));
        Assert.Matches("^[0-9a-f]{32}$", problem.Str("correlationId"));
        AssertNoInternals(reply);
        return problem;
    }

    private static void AssertNoInternals(Reply reply)
    {
        foreach (var leak in new[] { "SqlException", "at Kantar.", "   at ", "StackTrace", "System.", "Microsoft.", "Exception", "inv.", "sales.SalesOrder", "dbo." })
        {
            Assert.DoesNotContain(leak, reply.Text, StringComparison.Ordinal);
        }
    }

    private static string[] ErrorFields(JsonNode problem) =>
        problem["errors"]?.AsArray().Select(e => e!.Str("field")).ToArray() ?? [];

    [Fact]
    public async Task The_reference_body_is_accepted_so_every_refusal_below_is_caused_by_the_one_change()
    {
        var desk = await DeskAsync();

        var created = await PostOrderAsync(desk, desk.Body());

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("application/json; charset=utf-8", created.ContentType);
        Assert.Equal(1, await OrderCountAsync(desk));
    }

    [Theory]
    [InlineData("", ""","foo":1 """, "foo")]
    [InlineData("", ""","Note":"büyük harfle" """, "Note")]
    [InlineData(""","colour":"red" """, "", "lines[0].colour")]
    public async Task An_unknown_field_is_refused_and_named(string lineExtra, string extra, string expectedPath)
    {
        var desk = await DeskAsync();

        var problem = AssertCleanProblem(await PostOrderAsync(desk, desk.Body(lineExtra, extra)), HttpStatusCode.BadRequest, "unknown_field");

        var error = Assert.Single(problem["errors"]!.AsArray())!;
        Assert.Equal("unknown_field", error.Str("code"));
        Assert.Equal("$." + expectedPath, error.Str("field"));
        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Theory]
    [InlineData(""","unitPrice":0.01 """, "")]
    [InlineData(""","lineTotal":0 """, "")]
    [InlineData(""","sku":"SAHTE-1" """, "")]
    [InlineData("", ""","status":"Confirmed" """)]
    [InlineData("", ""","ownerUserId":"0199c0de-0000-7000-8000-000000000001" """)]
    [InlineData("", ""","number":"SO-00000001" """)]
    [InlineData("", ""","total":1 """)]
    [InlineData("", ""","id":"0199c0de-0000-7000-8000-000000000002" """)]
    [InlineData("", ""","customerName":"Başka Müşteri" """)]
    [InlineData("", ""","etag":"\"AAAAAAAAB9E=\"" """)]
    public async Task Mass_assignment_of_server_owned_fields_is_an_unknown_field_and_creates_nothing(string lineExtra, string extra)
    {
        var desk = await DeskAsync();

        AssertCleanProblem(await PostOrderAsync(desk, desk.Body(lineExtra, extra)), HttpStatusCode.BadRequest, "unknown_field");

        Assert.Equal(0, await OrderCountAsync(desk));
        Assert.Equal(0, await _world.AuditCountAsync(desk.User.Id, "sales.order_created"));
    }

    [Fact]
    public async Task Server_owned_fields_cannot_be_set_through_the_other_write_endpoints_either()
    {
        var desk = await DeskAsync();
        var (order, etag) = await World.CreateSalesOrderAsync(desk.Client, desk.Customer.Id, desk.Warehouse.Id, (desk.Product.Id, 1));

        var edit = await Reply.ReadAsync(desk.Client.SendAsync(HttpMethod.Put, $"/api/sales/orders/{order.Id()}", ifMatch: etag, rawBody: desk.Body(extra: ""","status":"Confirmed" """)));
        AssertCleanProblem(edit, HttpStatusCode.BadRequest, "unknown_field");
        var cancel = await Reply.ReadAsync(desk.Client.SendAsync(
            HttpMethod.Post, $"/api/sales/orders/{order.Id()}/cancel", idempotencyKey: ApiClient.NewKey(), ifMatch: etag, rawBody: """{"reason":"vazgeçildi","cancelledQty":0}"""));
        AssertCleanProblem(cancel, HttpStatusCode.BadRequest, "unknown_field");
        var purchase = await Reply.ReadAsync(desk.Client.SendAsync(
            HttpMethod.Post, "/api/purchasing/orders", idempotencyKey: ApiClient.NewKey(),
            rawBody: $$"""{"supplierId":"{{Guid.NewGuid()}}","warehouseId":"{{desk.Warehouse.Id}}","approvedBy":"{{desk.User.Id}}","lines":[]}"""));
        AssertCleanProblem(purchase, HttpStatusCode.BadRequest, "unknown_field");

        using var stored = await desk.Client.GetAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal("Draft", (await stored.JsonAsync(HttpStatusCode.OK)).Str("status"));
        Assert.Equal(etag, stored.ETag());
    }

    [Theory]
    [InlineData("", ""","warehouseId":"0199c0de-0000-7000-8000-000000000003" """)]
    [InlineData("", ""","note":"a","note":"b" """)]
    [InlineData(""","quantity":2 """, "")]
    public async Task A_duplicate_key_is_refused(string lineExtra, string extra)
    {
        var desk = await DeskAsync();

        var problem = AssertCleanProblem(await PostOrderAsync(desk, desk.Body(lineExtra, extra)), HttpStatusCode.BadRequest, "duplicate_field");

        Assert.Equal("duplicate_field", Assert.Single(problem["errors"]!.AsArray())!.Str("code"));
        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("\"1\"")]
    [InlineData("1e0")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("99999999999999999999")]
    public async Task A_quantity_must_be_a_json_integer(string quantity)
    {
        var desk = await DeskAsync();

        var problem = AssertCleanProblem(await PostOrderAsync(desk, desk.Body(quantity: quantity)), HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal(["$.lines[0].quantity"], ErrorFields(problem));
        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("100001")]
    public async Task An_integer_quantity_outside_the_business_range_is_a_validation_error(string quantity)
    {
        var desk = await DeskAsync();

        var problem = AssertCleanProblem(await PostOrderAsync(desk, desk.Body(quantity: quantity)), HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal(["lines[0].quantity"], ErrorFields(problem));
        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"customerId":""")]
    [InlineData("""{"customerId":"0199c0de-0000-7000-8000-000000000001","warehouseId":"0199c0de-0000-7000-8000-000000000002","lines":[],}""")]
    [InlineData("""{'customerId':'x'}""")]
    [InlineData("""/* yorum */ {}""")]
    [InlineData("<order/>")]
    [InlineData("customerId=1&warehouseId=2")]
    public async Task Malformed_json_is_invalid_json(string body)
    {
        var desk = await DeskAsync();

        AssertCleanProblem(await PostOrderAsync(desk, body), HttpStatusCode.BadRequest, "invalid_json");

        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Fact]
    public async Task Deeply_nested_json_is_refused_as_a_plain_bad_request()
    {
        var desk = await DeskAsync();
        string[] bodies =
        [
            string.Concat(Enumerable.Repeat("""{"lines":[""", 40)) + string.Concat(Enumerable.Repeat("]}", 40)),
            new string('[', 5_000) + new string(']', 5_000),
            desk.Body(extra: $""","note":{new string('[', 200)}{new string(']', 200)} """),
        ];

        foreach (var body in bodies)
        {
            var reply = await PostOrderAsync(desk, body);
            Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
            Assert.True(reply.IsProblem);
            Assert.Matches("^[0-9a-f]{32}$", reply.Json.Str("correlationId"));
            AssertNoInternals(reply);
        }

        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Fact]
    public async Task Missing_required_members_and_wrong_types_are_validation_errors()
    {
        var desk = await DeskAsync();
        string[] bodies =
        [
            """{}""",
            $$"""{"warehouseId":"{{desk.Warehouse.Id}}","lines":[]}""",
            $$"""{"customerId":"{{desk.Customer.Id}}","warehouseId":"{{desk.Warehouse.Id}}"}""",
            $$"""{"customerId":"not-a-guid","warehouseId":"{{desk.Warehouse.Id}}","lines":[]}""",
            $$$"""{"customerId":"{{{desk.Customer.Id}}}","warehouseId":"{{{desk.Warehouse.Id}}}","lines":{}}""",
            $$"""{"customerId":"{{desk.Customer.Id}}","warehouseId":"{{desk.Warehouse.Id}}","lines":[{"quantity":1}]}""",
            $$"""{"customerId":"{{desk.Customer.Id}}","warehouseId":"{{desk.Warehouse.Id}}","lines":[],"note":5}""",
        ];

        foreach (var body in bodies)
        {
            AssertCleanProblem(await PostOrderAsync(desk, body), HttpStatusCode.BadRequest, "validation_failed");
        }

        // An explicit null where the contract does not say "optional" is the same error as leaving the member out.
        var nulls = new (string Body, string Field)[]
        {
            ($$"""{"customerId":"{{desk.Customer.Id}}","warehouseId":"{{desk.Warehouse.Id}}","lines":null}""", "$.lines"),
            ($$"""{"customerId":null,"warehouseId":"{{desk.Warehouse.Id}}","lines":[]}""", "$.customerId"),
            ($$"""{"customerId":"{{desk.Customer.Id}}","warehouseId":"{{desk.Warehouse.Id}}","lines":[null]}""", "$.lines[0]"),
        };
        foreach (var (body, field) in nulls)
        {
            var reply = await PostOrderAsync(desk, body);
            var problem = AssertCleanProblem(reply, HttpStatusCode.BadRequest, "validation_failed");
            Assert.True(ErrorFields(problem).SequenceEqual([field]), reply.Text);
        }

        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Fact]
    public async Task A_null_array_element_is_a_validation_error_on_every_body_endpoint()
    {
        var desk = await DeskAsync();
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var (order, _) = await World.CreateSalesOrderAsync(desk.Client, desk.Customer.Id, desk.Warehouse.Id, (desk.Product.Id, 1));
        var (w, p) = (desk.Warehouse.Id, desk.Product.Id);
        var cases = new (ApiClient Client, HttpMethod Method, string Url, string Body, string Field)[]
        {
            (desk.Client, HttpMethod.Put, $"/api/sales/orders/{order.Id()}", $$"""{"customerId":"{{desk.Customer.Id}}","warehouseId":"{{w}}","lines":[null]}""", "$.lines[0]"),
            (desk.Client, HttpMethod.Post, $"/api/sales/orders/{order.Id()}/shipments", """{"dispatchReference":"SEVK-NULL-1","lines":[null]}""", "$.lines[0]"),
            (desk.Client, HttpMethod.Post, "/api/purchasing/orders", $$"""{"supplierId":"{{Guid.NewGuid()}}","warehouseId":"{{w}}","lines":[{"productId":"{{p}}","quantity":1,"unitCost":1.00},null]}""", "$.lines[1]"),
            (desk.Client, HttpMethod.Post, $"/api/purchasing/orders/{Guid.NewGuid()}/receipts", """{"externalReference":"IRS-NULL-1","lines":[null,null]}""", "$.lines[0]"),
            (desk.Client, HttpMethod.Post, "/api/inventory/transfers", $$"""{"reference":"TR-NULL-1","fromWarehouseId":"{{w}}","toWarehouseId":"{{Guid.NewGuid()}}","lines":[null]}""", "$.lines[0]"),
            (desk.Client, HttpMethod.Post, "/api/inventory/adjustments", $$"""{"reference":"AD-NULL-1","warehouseId":"{{w}}","reasonCode":"Found","note":"Sayım farkı tutanağı","lines":[null]}""", "$.lines[0]"),
            (admin, HttpMethod.Put, $"/api/admin/users/{desk.User.Id}/roles", """{"roles":["SalesAgent",null]}""", "$.roles[1]"),
            (admin, HttpMethod.Put, $"/api/admin/users/{desk.User.Id}/warehouse-grants", """{"grants":[null]}""", "$.grants[0]"),
            (admin, HttpMethod.Post, "/api/admin/users", """{"userName":"null.eleman","displayName":"Null Eleman","roles":[null]}""", "$.roles[0]"),
        };

        foreach (var (client, method, url, body, field) in cases)
        {
            var reply = await Reply.ReadAsync(client.SendAsync(method, url, idempotencyKey: ApiClient.NewKey(), ifMatch: "\"AAAAAAAAB9E=\"", rawBody: body));
            Assert.True(reply.Status == HttpStatusCode.BadRequest, $"{method} {url}: {(int)reply.Status} {reply.Text}");
            var problem = AssertCleanProblem(reply, HttpStatusCode.BadRequest, "validation_failed");
            Assert.True(ErrorFields(problem).SequenceEqual([field]), $"{method} {url}: {reply.Text}");
        }

        // The session of the user whose roles were "edited" is still alive: nothing was changed.
        using var me = await desk.Client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(0, await _world.CountAsync("SELECT COUNT(*) FROM iam.Users WHERE UserName = N'null.eleman';"));
    }

    [Fact]
    public async Task A_body_over_64_kilobytes_is_payload_too_large_and_one_exactly_at_the_limit_is_read()
    {
        var desk = await DeskAsync();
        const int limit = 64 * 1024;
        var body = desk.Body();
        Assert.Equal(body.Length, Encoding.UTF8.GetByteCount(body));

        // Insignificant white space pads the same valid document to an exact size.
        var atLimit = body + new string(' ', limit - body.Length);
        var overLimit = atLimit + " ";

        AssertCleanProblem(await PostOrderAsync(desk, overLimit), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        Assert.Equal(0, await OrderCountAsync(desk));
        AssertCleanProblem(await PostOrderAsync(desk, desk.Body(extra: $""","note":"{new string('n', 70_000)}" """)), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        Assert.Equal(0, await OrderCountAsync(desk));

        Assert.Equal(HttpStatusCode.Created, (await PostOrderAsync(desk, atLimit)).Status);
        Assert.Equal(1, await OrderCountAsync(desk));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/xml")]
    [InlineData("text/json")]
    [InlineData("application/jsonx")]
    public async Task A_body_that_is_not_declared_as_json_is_unsupported_media_type(string contentType)
    {
        var desk = await DeskAsync();

        // The bytes are a perfectly valid order; only the declared type is wrong.
        AssertCleanProblem(await PostOrderAsync(desk, desk.Body(), contentType), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Fact]
    public async Task Every_body_endpoint_refuses_a_non_json_body_and_an_unknown_route_stays_not_found()
    {
        var desk = await DeskAsync();
        var (order, etag) = await World.CreateSalesOrderAsync(desk.Client, desk.Customer.Id, desk.Warehouse.Id, (desk.Product.Id, 1));
        var targets = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Put, $"/api/sales/orders/{order.Id()}"),
            (HttpMethod.Post, $"/api/sales/orders/{order.Id()}/cancel"),
            (HttpMethod.Post, $"/api/sales/orders/{order.Id()}/shipments"),
            (HttpMethod.Post, "/api/purchasing/orders"),
            (HttpMethod.Put, $"/api/purchasing/orders/{Guid.NewGuid()}"),
            (HttpMethod.Post, $"/api/purchasing/orders/{Guid.NewGuid()}/receipts"),
            (HttpMethod.Post, "/api/inventory/transfers"),
            (HttpMethod.Post, "/api/inventory/adjustments"),
            (HttpMethod.Post, "/api/products"),
            (HttpMethod.Post, "/api/auth/change-password"),
        };

        foreach (var (method, url) in targets)
        {
            var reply = await Reply.ReadAsync(desk.Client.SendAsync(method, url, idempotencyKey: ApiClient.NewKey(), ifMatch: etag, rawBody: desk.Body(), contentType: "text/plain"));
            Assert.True(reply.Status == HttpStatusCode.UnsupportedMediaType, $"{method} {url}: {(int)reply.Status} {reply.Text}");
            AssertCleanProblem(reply, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        }

        // Declaring JSON does not conjure up a route.
        var unknown = await Reply.ReadAsync(desk.Client.SendAsync(HttpMethod.Post, "/api/sales/invoices", idempotencyKey: ApiClient.NewKey(), rawBody: desk.Body()));
        AssertCleanProblem(unknown, HttpStatusCode.NotFound, "not_found");

        using var stored = await desk.Client.GetAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal(etag, stored.ETag());
        Assert.Equal(1, await OrderCountAsync(desk));
    }

    [Fact]
    public async Task Json_with_a_charset_parameter_is_accepted()
    {
        var desk = await DeskAsync();

        Assert.Equal(HttpStatusCode.Created, (await PostOrderAsync(desk, desk.Body(), "application/json; charset=utf-8")).Status);
    }

    [Fact]
    public async Task Unknown_or_repeated_query_parameters_are_refused()
    {
        var desk = await DeskAsync();
        var (order, _) = await World.CreateSalesOrderAsync(desk.Client, desk.Customer.Id, desk.Warehouse.Id, (desk.Product.Id, 1));
        var cases = new (string Url, string Field)[]
        {
            ("/api/sales/orders?foo=1", "foo"),
            ("/api/sales/orders?Limit=5", "Limit"),
            ("/api/sales/orders?limit=5&limit=6", "limit"),
            ("/api/sales/orders?sort=total", "sort"),
            ("/api/sales/orders?orderBy=1;DROP%20TABLE%20x", "orderBy"),
            ($"/api/sales/orders/{order.Id()}?expand=owner", "expand"),
            ($"/api/sales/orders/{order.Id()}/summary?format=pdf", "format"),
            ("/api/purchasing/orders?supplierId=1", "supplierId"),
            ($"/api/inventory/balances?warehouseId={desk.Warehouse.Id}&all=true", "all"),
            ($"/api/inventory/movements?warehouseId={desk.Warehouse.Id}&actor=me", "actor"),
            ($"/api/inventory/reservations?warehouseId={desk.Warehouse.Id}&type=Reserve", "type"),
            ("/api/products?price=1", "price"),
            ("/api/customers?search=a", "search"),
            ("/api/warehouses?active=true", "active"),
            ("/api/auth/me?debug=1", "debug"),
        };

        foreach (var (url, field) in cases)
        {
            var reply = await Reply.ReadAsync(desk.Client.GetAsync(url));
            var problem = AssertCleanProblem(reply, HttpStatusCode.BadRequest, "unknown_parameter");
            Assert.True(ErrorFields(problem).SequenceEqual([field]), $"{url}: {reply.Text}");
        }

        // A parameter name is echoed back only up to a bound.
        var longName = new string('x', 500);
        var bounded = AssertCleanProblem(await Reply.ReadAsync(desk.Client.GetAsync($"/api/sales/orders?{longName}=1")), HttpStatusCode.BadRequest, "unknown_parameter");
        Assert.Equal(40, Assert.Single(ErrorFields(bounded)).Length);
    }

    [Fact]
    public async Task Page_size_is_one_to_one_hundred()
    {
        var desk = await DeskAsync();
        string[] lists =
        [
            "/api/sales/orders?",
            "/api/purchasing/orders?",
            "/api/products?",
            $"/api/inventory/balances?warehouseId={desk.Warehouse.Id}&",
            $"/api/inventory/movements?warehouseId={desk.Warehouse.Id}&",
            $"/api/inventory/reservations?warehouseId={desk.Warehouse.Id}&",
            $"/api/inventory/transfers?warehouseId={desk.Warehouse.Id}&",
            $"/api/inventory/adjustments?warehouseId={desk.Warehouse.Id}&",
        ];

        // The purchasing list needs the purchasing role; this desk reads everything else.
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(desk.Warehouse.Id, WarehouseLevel.View)]);
        foreach (var list in lists)
        {
            var client = list.Contains("purchasing", StringComparison.Ordinal) ? buyer : desk.Client;
            foreach (var bad in new[] { "0", "101", "-1", "1000000", "abc", "1.0", "+5", "%201", "99999999999" })
            {
                var reply = await Reply.ReadAsync(client.GetAsync($"{list}limit={bad}"));
                var problem = AssertCleanProblem(reply, HttpStatusCode.BadRequest, "validation_failed");
                Assert.True(ErrorFields(problem).SequenceEqual(["limit"]), $"{list}limit={bad}: {reply.Text}");
            }

            foreach (var good in new[] { "1", "100" })
            {
                using var response = await client.GetAsync($"{list}limit={good}");
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{list}limit={good}: {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task A_cursor_that_this_api_did_not_issue_is_refused()
    {
        var desk = await DeskAsync();
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(desk.Warehouse.Id, WarehouseLevel.View)]);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var lists = new (ApiClient Client, string Url)[]
        {
            (desk.Client, "/api/sales/orders?"),
            (buyer, "/api/purchasing/orders?"),
            (desk.Client, "/api/products?"),
            (desk.Client, $"/api/inventory/balances?warehouseId={desk.Warehouse.Id}&"),
            (desk.Client, $"/api/inventory/movements?warehouseId={desk.Warehouse.Id}&"),
            (desk.Client, $"/api/inventory/reservations?warehouseId={desk.Warehouse.Id}&"),
            (desk.Client, $"/api/inventory/transfers?warehouseId={desk.Warehouse.Id}&"),
            (desk.Client, $"/api/inventory/adjustments?warehouseId={desk.Warehouse.Id}&"),
            (admin, "/api/admin/audit?"),
        };
        string[] garbage = ["garbage!", "%00", "..%2F..%2Fetc%2Fpasswd", "bm90LWEtY3Vyc29y", "'%20OR%201=1--", new string('A', 4000)];

        foreach (var (client, url) in lists)
        {
            foreach (var cursor in garbage)
            {
                var reply = await Reply.ReadAsync(client.GetAsync($"{url}cursor={cursor}"));
                var problem = AssertCleanProblem(reply, HttpStatusCode.BadRequest, "validation_failed");
                Assert.True(ErrorFields(problem).SequenceEqual(["cursor"]), $"{url}cursor={cursor[..Math.Min(20, cursor.Length)]}: {reply.Text}");
            }
        }
    }

    [Fact]
    public async Task Stock_queries_need_a_warehouse_and_filters_take_only_documented_values()
    {
        var desk = await DeskAsync();
        var w = desk.Warehouse.Id;
        var cases = new (string Url, string Field)[]
        {
            ("/api/inventory/balances", "warehouseId"),
            ("/api/inventory/balances?warehouseId=", "warehouseId"),
            ("/api/inventory/balances?lowStockOnly=true", "warehouseId"),
            ("/api/inventory/movements", "warehouseId"),
            ("/api/inventory/reservations", "warehouseId"),
            ("/api/inventory/transfers", "warehouseId"),
            ("/api/inventory/adjustments", "warehouseId"),
            ("/api/inventory/balances?warehouseId=not-a-guid", "warehouseId"),
            ("/api/inventory/balances?warehouseId=*", "warehouseId"),
            ($"/api/inventory/balances?warehouseId={w}&lowStockOnly=yes", "lowStockOnly"),
            ($"/api/inventory/balances?warehouseId={w}&lowStockOnly=1", "lowStockOnly"),
            ($"/api/inventory/balances?warehouseId={w}&search={new string('s', 65)}", "search"),
            ($"/api/inventory/movements?warehouseId={w}&type=Theft", "type"),
            ($"/api/inventory/movements?warehouseId={w}&type=1", "type"),
            ($"/api/inventory/movements?warehouseId={w}&type=receipt", "type"),
            ($"/api/inventory/movements?warehouseId={w}&productId=abc", "productId"),
            ($"/api/inventory/movements?warehouseId={w}&from=yesterday", "from"),
            ($"/api/inventory/movements?warehouseId={w}&to=31-31-2026", "to"),
            ("/api/sales/orders?status=Open", "status"),
            ("/api/sales/orders?status=2", "status"),
            ("/api/sales/orders?mine=evet", "mine"),
            ("/api/sales/orders?warehouseId=1", "warehouseId"),
            ("/api/products?active=maybe", "active"),
        };

        foreach (var (url, field) in cases)
        {
            var reply = await Reply.ReadAsync(desk.Client.GetAsync(url));
            var problem = AssertCleanProblem(reply, HttpStatusCode.BadRequest, "validation_failed");
            Assert.True(ErrorFields(problem).SequenceEqual([field]), $"{url}: {reply.Text}");
        }
    }

    [Theory]
    [InlineData("\\u0000")]
    [InlineData("\\u0007")]
    [InlineData("\\u001b")]
    [InlineData("\\u007f")]
    [InlineData("\\u0085")]
    [InlineData("\\u200b")]
    [InlineData("\\u202e")]
    [InlineData("\\ufeff")]
    public async Task Control_and_invisible_characters_in_a_note_are_refused(string escaped)
    {
        var desk = await DeskAsync();

        var problem = AssertCleanProblem(
            await PostOrderAsync(desk, desk.Body(extra: $""","note":"teslimat{escaped}notu" """)), HttpStatusCode.BadRequest, "validation_failed");

        var error = Assert.Single(problem["errors"]!.AsArray())!;
        Assert.Equal(("note", "invalid_characters"), (error.Str("field"), error.Str("code")));
        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Fact]
    public async Task A_note_is_trimmed_normalised_and_may_contain_line_breaks_and_markup_as_plain_text()
    {
        var desk = await DeskAsync();

        // "C + combining cedilla" must come back precomposed; the tag is stored and returned as text, never interpreted.
        var created = await PostOrderAsync(desk, desk.Body(extra: ""","note":"  Çift kapı\r\n\t<script>alert(1)</script>  " """));

        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("Çift kapı\n\t<script>alert(1)</script>", created.Json.Str("note"));

        var tooLong = await PostOrderAsync(desk, desk.Body(extra: $""","note":"{new string('n', 501)}" """));
        Assert.Equal(["note"], ErrorFields(AssertCleanProblem(tooLong, HttpStatusCode.BadRequest, "validation_failed")));

        // An explicit null for an optional member is the same as leaving it out.
        var withNull = await PostOrderAsync(desk, desk.Body(extra: ""","note":null """));
        Assert.Equal(HttpStatusCode.Created, withNull.Status);
        Assert.Null(withNull.Json["note"]);
    }

    [Fact]
    public async Task Search_text_is_matched_literally_and_hostile_text_is_only_data()
    {
        var desk = await DeskAsync();
        await _world.StockAsync(desk.Warehouse, desk.Product, 3);
        var balances = $"/api/inventory/balances?warehouseId={desk.Warehouse.Id}&search=";

        // The one stocked product is found by its SKU prefix and by a part of its name …
        Assert.Single((await desk.Client.GetJsonAsync(balances + desk.Product.Sku))["items"]!.AsArray());
        Assert.Single((await desk.Client.GetJsonAsync(balances + Uri.EscapeDataString("Ürün")))["items"]!.AsArray());

        // … and by nothing that would only match if the text were treated as a pattern or as SQL.
        foreach (var term in new[] { "%", "_", "[A-Z]%", "SKU%", "%25", "' OR 1=1 --", "'; DROP TABLE inv.StockBalances; --", "\\", "😀", "*" })
        {
            var encoded = Uri.EscapeDataString(term);
            foreach (var url in new[] { balances + encoded, "/api/products?search=" + encoded })
            {
                var reply = await Reply.ReadAsync(desk.Client.GetAsync(url));
                Assert.True(reply.Status == HttpStatusCode.OK, $"search={encoded}: {(int)reply.Status} {reply.Text}");
                Assert.True(reply.Json["items"]!.AsArray().Count == 0, $"{url} matched {reply.Json["items"]!.AsArray().Count} rows");
            }
        }

        // Control and invisible characters are refused like in any other text. (A NUL in a LIKE pattern
        // is ignored by the collation, so "search=%00" used to return every row.)
        foreach (var term in new[] { "\0", "a\0", "\u0007", "\u001b[2J", "​", "sku‮" })
        {
            var encoded = Uri.EscapeDataString(term);
            foreach (var url in new[] { balances + encoded, "/api/products?search=" + encoded })
            {
                var reply = await Reply.ReadAsync(desk.Client.GetAsync(url));
                var problem = AssertCleanProblem(reply, HttpStatusCode.BadRequest, "validation_failed");
                Assert.True(ErrorFields(problem).SequenceEqual(["search"]), $"{url}: {reply.Text}");
            }
        }

        Assert.Equal((3, 0), await _world.BalanceAsync(desk.Warehouse.Id, desk.Product.Id));
    }

    [Theory]
    [InlineData("\\ud800")]
    [InlineData("\\udc00x")]
    [InlineData("\\uZZZZ")]
    [InlineData("\\x41")]
    public async Task A_string_that_is_not_valid_text_is_a_bad_request_not_a_server_error(string escaped)
    {
        var desk = await DeskAsync();

        var reply = await PostOrderAsync(desk, desk.Body(extra: $""","note":"a{escaped}b" """));

        Assert.True(reply.Status == HttpStatusCode.BadRequest, $"{(int)reply.Status} {reply.Text}");
        Assert.True(reply.IsProblem);
        Assert.Contains(reply.Problem, new[] { "invalid_json", "validation_failed" });
        AssertNoInternals(reply);
        Assert.Equal(0, await OrderCountAsync(desk));
    }

    [Fact]
    public async Task Unknown_routes_and_ids_that_are_not_guids_are_clean_not_found_problems()
    {
        var desk = await DeskAsync();

        foreach (var url in new[] { "/api/nope", "/api/sales/orders/123", "/api/sales/orders/not-a-guid/confirm", "/api/inventory", "/api/admin/debug", "/api/failpoints" })
        {
            AssertCleanProblem(await Reply.ReadAsync(desk.Client.GetAsync(url)), HttpStatusCode.NotFound, "not_found");
        }
    }
}
