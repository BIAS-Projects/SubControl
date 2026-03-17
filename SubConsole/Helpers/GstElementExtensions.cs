using Gst;

namespace SubConsole.Helpers
{
    public static class GstElementExtensions
    {
        public static void TrySet(this Element element, string property, object value)
        {
            try
            {
                element[property] = value;
            }
            catch
            {
                // Property not supported → ignore safely
            }
        }
    }
}