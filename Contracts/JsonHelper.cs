using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Contracts
{
    public static class JsonHelper
    {
        public static readonly JsonSerializerOptions DefaultOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}
