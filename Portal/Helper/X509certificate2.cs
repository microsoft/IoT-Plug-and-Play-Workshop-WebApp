using System.Text;

namespace Portal.Helper
{
    internal class X509certificate2
    {
        private StringBuilder certContents;

        public X509certificate2(StringBuilder certContents)
        {
            this.certContents = certContents;
        }
    }
}