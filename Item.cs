using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TodoApi;

public partial class Item
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public bool? IsComplete { get; set; }

    // The owner of the task. Set by the server from the JWT, never sent to or accepted from the client.
    [JsonIgnore]
    public int? UserId { get; set; }
}
