# Liquidity Bot for NebliDex Trader API
A simple market maker bot that works with the NebliDex Trader API. Built on Windows but using Mono, it can be built on Mac and Linux. Must be ran with NebliDex client, version at least **11.0.1.**

# How to Use
This bot simply takes the balances of your NebliDex wallets and attempts to market make profit for you using spread differences and by taking advantage of maker rebates. It will automatically close and repost orders when the price of the base or trade asset changes more than 5%. You set the markets you want to trade in and load up your wallet balance to use the bot.

You can use this bot to learn how to make bots for NebliDex or use this bot to trade. Adjust values specified by the code to customize the bot. There is no binary available, you must build this bot from source.

### Building Liquidity Bot
* Make sure at least .NET Framework 4.5 is installed on your system
* Find your favorite C# code editor (Visual Studio, SharpDevelop, MonoDevelop)
* Open Solution
* Build and Run in Terminal
