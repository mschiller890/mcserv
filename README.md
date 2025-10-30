# MCServ
MCServ is a lightweight and user-friendly Minecraft server manager designed to make hosting and managing servers simple. It provides an intuitive interface for starting, stopping, and configuring Minecraft servers — no command-line knowledge required.

## How It Works
To make your Minecraft server accessible to friends, MCServ uses **ngrok** to create a secure TCP tunnel from your local machine to the internet.

## Setup Guide

### 1) Download
- You can either build MCServ yourself or download a prebuilt release from the **Releases** section.

### 2) Get ngrok
- Download ngrok using the button in the bottom-right corner of the app or from the [ngrok website](https://ngrok.com/).
- Create a free account and copy your **authtoken**.
- Paste the authtoken into the textbox in MCServ, then click **Add Authtoken**.

### 3) Create a Server
- Choose a name for your server.
- Paste the URL of your desired `server.jar`.
- Click **Create** to generate your server instance.

### 4) Run the Server
- Under the **Runtime** section, click **Start** to launch your server.
- To make your server public, click **Start ngrok** in the **ngrok** section.
- Monitor logs and potential errors in the **Server Console** and **ngrok Console** tabs.

---

MCServ handles the technical setup so you can focus on what matters most — playing Minecraft with your friends.
