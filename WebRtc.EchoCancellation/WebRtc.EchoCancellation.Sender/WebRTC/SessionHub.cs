using Microsoft.AspNetCore.SignalR;

namespace WebRtc.EchoCancellation.Server.WebRTC
{
    public class SessionHub : Hub
    {
        public async Task SendOffer(string offer)
        {
            await Clients.Others.SendAsync("ReceiveOffer", offer);
        }

        public async Task SendAnswer(string answer)
        {
            await Clients.Others.SendAsync("ReceiveAnswer", answer);
        }

        public async Task SendIceCandidate(string candidate)
        {
            await Clients.Others.SendAsync("ReceiveIceCandidate", candidate);
        }
    }
}
