using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms.VisualStyles;

namespace MultiPass
{
    /// <summary>
    /// <para>Stores the expected replies from a sent command along with if it's expecting a reply when parameters are sent.</para>
    /// <para></para>
    /// </summary>
    public class ExpectedReply
    {
        /// <summary>
        /// <para>What replies are expected.</para>
        /// <para>If replies.Length == 0 then there are never any replies.</para>
        /// <para>e.g. audioEnable never returns anything. So the number of any expected replies is 0.</para>
        /// </summary>
        public string[] replies = new string[] { };
        /// <summary>
        /// <para>Is a reply expected when parameters are sent with the command.</para>
        /// <para>e.g. send&gt;Feynman = 4 | receive&gt; Feynman = 0,0,0 | replyExpected == true.</para>
        /// <para>e.g. send&gt;analysisMask = 0x7FFF | receive&gt; &lt;no reply&gt;| replyExpected == false.</para>
        /// <para></para>
        /// </summary>
        public bool replyAlwaysExpected = false;

        /// <summary>
        /// <para>Does the command sometimes have input parameters?</para>
        /// <para>e.g. cStatus never has any input parameters. It is only a query. isOnlyQuery == true.</para>
        /// <para>e.g. channelMask is both a query and a command. isOnlyQuery == false.</para>
        /// </summary>
        public bool isOnlyQuery = false;

        public ExpectedReply(string[] replies, bool replyAlwaysExpected, bool isOnlyQuery) 
        { 
            this.replies = replies;
            this.replyAlwaysExpected = replyAlwaysExpected;
            this.isOnlyQuery = isOnlyQuery;
        }
    }
}
