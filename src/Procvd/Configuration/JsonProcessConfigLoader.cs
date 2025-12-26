// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Text.Json;
using Itexoft.IO.Streams.Chars;
using Itexoft.Threading;

namespace Procvd.Configuration;

public sealed class JsonProcessConfigLoader(JsonSerializerOptions? options = null) : IProcessConfigLoader
{
    private readonly JsonSerializerOptions optionsInternal = options ?? CreateDefaultOptions();

    public ProcessConfig Load(CharStreamBrws byteStream, CancelToken token = default)
    {
        if (byteStream.IsEmpty)
            throw new ArgumentNullException(nameof(byteStream));

        var config = JsonSerializer.Deserialize<ProcessConfig>(byteStream.ReadAllText(), this.optionsInternal);

        if (config is null)
            throw new ProcessConfigException("config deserialized to null");

        return config;
    }

    public static JsonSerializerOptions CreateDefaultOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
