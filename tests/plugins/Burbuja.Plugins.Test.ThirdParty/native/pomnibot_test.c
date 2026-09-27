#include <stdint.h>

/* Minimal real native library used by Burbuja.Plugins.Test.ThirdParty to prove that
   PluginLoadContext.LoadUnmanagedDll resolves native assets from the plugin folder. */
int32_t pomnibot_test_add(int32_t a, int32_t b)
{
    return a + b;
}
