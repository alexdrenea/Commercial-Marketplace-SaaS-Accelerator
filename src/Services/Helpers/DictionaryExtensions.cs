using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Marketplace.SaaS.Accelerator.Services.Helpers
{
    public static class DictionaryExtensions
    {
        public static TValue GetOrDefault<TKey, TValue>(this Dictionary<TKey,TValue> source, TKey key)
        {
            if (source.ContainsKey(key)) return source[key];

            return default(TValue);
        }
    }
}
