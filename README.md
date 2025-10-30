# MCServ
MCServ is a lightweight, no-nonsense Minecraft server manager built to make your life easier. Forget typing cryptic commands into terminals - MCServ gives you a clean, intuitive interface for starting, stopping, and configuring servers like a civilized human being.

## How It Works
When you want your friends to join, MCServ uses **ngrok** to spin up a secure TCP tunnel straight to your local server. Basically, it makes your computer say, “hey world, I’m a Minecraft server now,” without exposing your entire network.

## Setup Guide

### 1) Download
- Either build it yourself (for the DIY enjoyers) or grab a prebuilt release from the **Releases** section.  
- It’s small, clean, and doesn’t come with weird extras. Promise.

### 2) Get ngrok
- Download ngrok from the button in the bottom-right corner of MCServ or from the [official website](https://ngrok.com/).  
- Make an account, copy your **authtoken**, and paste it into the textbox in the app.  
- Hit **Add Authtoken** and boom - you’re in.

### 3) Create a Server
- Give your server a name (yes, it can be something cursed).  
- Paste in the URL to your `server.jar`.  
- Click **Create** and watch MCServ handle the setup magic.

### 4) Run the Server
- Under **Runtime**, click **Start** to launch your server.  
- Want friends to join? Hit **Start ngrok** under the **ngrok** section.  
- Keep an eye on the **Server Console** and **ngrok Console** tabs for logs, errors, and the occasional “oh no” moment.

---

MCServ doesn’t overcomplicate things - it just works. You bring the server jar, it brings the vibes. Now go forth and build your blocky empire.
