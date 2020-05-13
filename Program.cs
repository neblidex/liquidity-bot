using System;
using System.Data;
using System.Xml;
using System.Configuration;
using System.Globalization;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Text;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// This is a simple market making Liquidity Bot used in conjunction with the NebliDex trading engine
// Features of this bot include:
// - Creating 2 buy and 2 sell orders in desired markets with a certain spread amount
// - Amount is based on desired percentage of wallet and minAmount is a desired percentage of amount
// - If price discovery is required (no open orders), NDEX has a minimum USD ask price and separate maximum bid price and all other pairs are pulled from CoinGecko
// - The orders will close and reopen if the prices (pulled from centralized platforms) change quickly
// - If spread is less than current spread, spread will expand to be average price between highest to lowest on the ask and bid

// - CoinGecko is used for finding prices of various coins
// IDs of certain coins (vs currency "usd")
// - BTC = "bitcoin"
// - LTC = "litecoin"
// - USDC = "usd-coin"
// - GRS = "groestlcoin"
// - BCH = "bitcoin-cash"
// - ETH = "ethereum"
// - NEBL = "neblio"
// - MONA = "monacoin"
// - DAI = "dai"

namespace LiquidityBot
{
	class Program
	{
		public static string coinGeckoEndpoint = "https://api.coingecko.com/api/v3/simple/price/";
		public static string traderAPIEndpoint = "http://localhost:6328/"; // Address to the localhost Trader server
		public static int maximumOrders = 48; // The maximum amount of open orders possible on NebliDex, set by total markets
		public static int currentMarket = 0; // Bot checks a market from the list every 1 minute
		
		public static Timer PeriodicTimer;
		public static bool program_running = true;
		
		// Customizable values
		public static decimal maximumNDEXBid = 0.0001m; // This is the maximum USD price we will buy NDEX for
		public static decimal minimumNDEXAsk = 0.02m; // This is the minimum USD price we will sell NDEX for		
		public static decimal maxWalletUtilize = 0.75m;  // The maximum percentage of coins in the wallet that can be utilized for trading
		public static decimal minAmountPercent = 0.5m; // The percentage of the amount that is the minAmount to trade
		public static decimal minTradeAmountUSD = 50m; // The smallest trade amount in USD, if wallet is smaller than this, will not open trade		
		
		public static List<Market> marketList = new List<Market>(); // A list of all markets traded
		
		public class Market
		{
			public string marketString; // ex: NDEX/NEBL, ETH/BTC
			public decimal desiredSpread; // Not a percent, 0.002 = 0.2%
			public decimal tradeUSDPrice = -1; // USD price of the trade item 
			public decimal baseUSDPrice = -1; // USD price of base item
			public string tradeGeckoID; // CoinGecko ID for trade item
			public string baseGeckoID; // CoinGecko ID for base item
			
			public Market(string ms, decimal spread, string tid, string bid){
				marketString = ms.ToUpper();
				desiredSpread = spread;
				tradeGeckoID = tid;
				baseGeckoID = bid;
			}
		}
		
		public class Order
		{
			public string marketString; // ex: NDEX/NEBL
			public string orderID; // The ID of our order
			public int orderType; // 0 = buy or 1 = sell
			public Order(string ms, string id, int type){
				marketString = ms.ToUpper();
				orderID = id;
				orderType = type;
			}
		}
		
		public static void Main(string[] args)
		{
			Console.WriteLine("Welcome to Liquidity Bot for NebliDex");
			LiquidityBotLog("-------------------------------");
			LiquidityBotLog("Running new instance of Liquidity Bot");
			bool ok = CheckAPIConnection();
			if(ok == true){
				Console.WriteLine("Loading the markets");
				LiquidityBotLog("Loading the markets");
				CreateMarkets();
				while(program_running == true){
					Console.ReadKey(true);
				}				
			}else{
				Console.WriteLine("Failed to connect to Trader API");
			}
			Console.Write("Press any key to exit . . . ");
			Console.ReadKey(true);
		}
		
		public static bool CheckAPIConnection()
		{
			// This will test the Trader API connection
			string rawdata = HttpRequest(traderAPIEndpoint,"request=ping",false,3);				
			if(rawdata.Length > 0){
				JObject js = JObject.Parse(rawdata);
				if(js["result"].ToString() == "Pong"){
					return true;
				}
			}
			return false;
		}
		
		public static void CreateMarkets()
		{
			// Create markets that we want to target
			// The markets must exist in NebliDex
			marketList.Add(new Market("NDEX/NEBL",0.1m,"neblidex","neblio"));
			marketList.Add(new Market("NDEX/BTC",0.1m,"neblidex","bitcoin"));
			marketList.Add(new Market("NDEX/LTC",0.1m,"neblidex","litecoin"));
			marketList.Add(new Market("NDEX/GRS",0.2m,"neblidex","groestlcoin"));
			marketList.Add(new Market("NDEX/MONA",0.2m,"neblidex","monacoin"));
			marketList.Add(new Market("NDEX/DAI",0.1m,"neblidex","dai"));
			marketList.Add(new Market("NDEX/USDC",0.1m,"neblidex","usd-coin"));
			marketList.Add(new Market("NDEX/BCH",0.1m,"neblidex","bitcoin-cash"));
			marketList.Add(new Market("NDEX/ETH",0.1m,"neblidex","ethereum"));
			marketList.Add(new Market("NEBL/BTC",0.1m,"neblio","bitcoin"));
			marketList.Add(new Market("ETH/BTC",0.1m,"ethereum","bitcoin"));
			marketList.Add(new Market("LTC/BTC",0.1m,"litecoin","bitcoin"));
			
			// Start the Timer
			Console.WriteLine("Markets loaded");
			PeriodicTimer = new Timer(new TimerCallback(CheckMarket),null,0,System.Threading.Timeout.Infinite); // Not ran again until called
		}
		
		public static async void CheckMarket(object state)
		{
			// This method checks 1 market per 5 minutes
			int function_time = UTCTime();
			try{
				Market mk = marketList[currentMarket];
				LiquidityBotLog("Checking the "+mk.marketString+" market");
				
				// Change the market to what we want and wait for the loading to complete
				bool ok = ChangeMarket(mk.marketString);
				if(ok == false){
					LiquidityBotLog("Failed to change market to "+mk.marketString);
					return;
				} // This should not happen
				
				bool market_loaded = false;
				for(int i = 0;i < 5;i++){
					// Check the market up to 5 times to see if it has changed
					string loaded_market = await Task.Run(() => GetCurrentMarket() );
					if(loaded_market == mk.marketString){
						market_loaded = true;
						break;
					}else{
						LiquidityBotLog("Waiting for market to change");
						await Task.Delay(1000*10); // Wait 10 seconds
					}
				}
				if(market_loaded == false){
					LiquidityBotLog("Failed to load market within appropriate time");
					return;
				}
				
				// First split the market string into symbols
				string[] symbols = mk.marketString.Split('/');
				
				// Get symbols USD values
				decimal trade_price = GetUSDPrice(mk.tradeGeckoID);
				decimal base_price = GetUSDPrice(mk.baseGeckoID);
				if(trade_price < 0 || base_price < 0){ LiquidityBotLog("Problem obtaining price"); return;}
				if(trade_price == 0){trade_price = 0.00000001m;}
				if(base_price == 0){base_price = 0.00000001m;}
				
				decimal trade_balance = GetWalletBalance(symbols[0]);
				decimal base_balance = GetWalletBalance(symbols[1]);
				if(trade_balance < 0 || base_balance < 0){ LiquidityBotLog("Problem obtaining balance"); return;}
				
				// Check my open orders and clear the list
				int total_market_buy = 0;
				int total_market_sell = 0;
				int my_total_orders = 0;
				List<Order> ordersList = new List<Order>(); // A list of all our orders for this market
						
				string respdata = HttpRequest(traderAPIEndpoint,"request=myOpenOrders",false,10);				
				if(respdata.Length > 0){
					JObject js = JObject.Parse(respdata);					
					int code = Convert.ToInt32(js["code"].ToString());
					if(code == 1){
						foreach (JToken ord in js["result"])
						{
							if(ord["market"].ToString() == mk.marketString){
								string orderType = ord["orderType"].ToString();
								int type = 0;
								if(orderType == "BUY" || orderType == "QUEUED BUY"){
									total_market_buy++;
									type = 0;
								}else{
									total_market_sell++;
									type = 1;
								}
								ordersList.Add(new Order(ord["market"].ToString(),ord["orderID"].ToString(),type));
							}
							my_total_orders++;
						}
					}
				}else{
					return;
				}
				
				// Now determine if the price has moved suddenly (more than 5% since last time scanned), then close if true
				if(mk.tradeUSDPrice < 0 || mk.baseUSDPrice < 0){
					// First time scanned, set the price
					mk.tradeUSDPrice = trade_price;
					mk.baseUSDPrice = base_price;
				}else{
					bool close_orders = false;
					if(Math.Abs((trade_price - mk.tradeUSDPrice) / mk.tradeUSDPrice) > 0.05m){
						close_orders = true;						
					}else if(Math.Abs((base_price - mk.baseUSDPrice) / mk.baseUSDPrice) > 0.05m){
						close_orders = true;
					}
					if(close_orders == true){
						mk.tradeUSDPrice = trade_price;
						mk.baseUSDPrice = base_price;
						LiquidityBotLog("Prices have changed, attempting to close current orders for market");
						for(int i = 0;i < ordersList.Count;i++){
							CloseMyOrder(ordersList[i].orderID);
						}
						return;
					}
				}
				
				if(my_total_orders > maximumOrders - 8){
					// Do not create more than a certain amount of orders
					return;
				}
				
				// Now get a list of all the open orders for the market
				decimal ask_price_max = -1;
				decimal ask_price_min = -1;
				
				decimal bid_price_max = -1;
				decimal bid_price_min = -1;
				
				decimal current_spread = -1;

				respdata = HttpRequest(traderAPIEndpoint,"request=marketDepth",false,10);				
				if(respdata.Length > 0){
					JObject js = JObject.Parse(respdata);
					int code = Convert.ToInt32(js["code"].ToString());
					if(code == 1){
						int count = 0;
						foreach (JToken ord in js["result"]["asks"])
						{
							if(count == 0){
								ask_price_max = decimal.Parse(ord["price"].ToString(),CultureInfo.InvariantCulture);
							}
							ask_price_min = decimal.Parse(ord["price"].ToString(),CultureInfo.InvariantCulture);
							count++;
						}
						count = 0;
						foreach (JToken ord in js["result"]["bids"])
						{
							if(count == 0){
								bid_price_max = decimal.Parse(ord["price"].ToString(),CultureInfo.InvariantCulture);
							}
							bid_price_min = decimal.Parse(ord["price"].ToString(),CultureInfo.InvariantCulture);
							count++;
						}
					}
				}else{
					return;
				}
				
				if(ask_price_min >= 0 && bid_price_min >= 0){
					// This market has a spread
					current_spread = (ask_price_min - bid_price_max) / bid_price_max;
				}

				// Check to see if opportunity to create a sell order first
				if(total_market_sell < 2){
					decimal wallet_value = trade_price * trade_balance;
					decimal target_price;
					if(symbols[0] == "NDEX"){
						// NDEX has a minimum ask price
						wallet_value = minimumNDEXAsk * trade_balance;
					}					
					if(wallet_value * maxWalletUtilize > minTradeAmountUSD){
						// Ok to sell
						if(current_spread > mk.desiredSpread){
							// We do not want to narrow the spread so we put our order in the middle if possible
							if(ask_price_max == ask_price_min){
								// Set our target price slightly above
								target_price = ask_price_max + ask_price_max * 0.25m;
							}else{
								// Put our order in the middle
								target_price = (ask_price_max + ask_price_min) / 2.0m;
							}
						}else if(current_spread <= mk.desiredSpread && current_spread >= 0){
							// Put the order at our requested spread
							target_price = bid_price_max + bid_price_max * mk.desiredSpread;
						}else{
							// No spread available
							target_price = Math.Round(trade_price / base_price,8);
							if(symbols[0] == "NDEX"){
								// NDEX has a minimum price in price discovery phase
								target_price = Math.Round(minimumNDEXAsk / base_price,8);
							}else{
								target_price = target_price + target_price * (mk.desiredSpread / 2);
							}							
						}
						target_price = Math.Round(target_price,8);
						decimal target_amount = trade_balance * maxWalletUtilize;
						decimal target_min_amount = target_amount * minAmountPercent;
						if(symbols[0] == "NDEX"){
							// NDEX is NTP1 so it is indivisible
							target_amount = Math.Floor(target_amount);
							target_min_amount = Math.Floor(target_min_amount);
						}
						bool good = await Task.Run(() => PostMakerOrder(1,target_price,target_amount,target_min_amount));
						if(good == false){
							LiquidityBotLog("Failed to post sell maker order to market");
							return;
						}
						if(total_market_sell == 0){
							// Post a second order slightly higher in price and lower in amount
							target_price = Math.Round(target_price + target_price * 0.5m,8);
							target_amount = target_amount - target_amount * 0.5m;
							target_min_amount = target_amount * minAmountPercent;
							if(symbols[0] == "NDEX"){
								// NDEX is NTP1 so it is indivisible
								target_amount = Math.Floor(target_amount);
								target_min_amount = Math.Floor(target_min_amount);
							}
							good = await Task.Run(() => PostMakerOrder(1,target_price,target_amount,target_min_amount));
							if(good == false){
								LiquidityBotLog("Failed to post second sell maker order to market");
								return;
							}							
						}
					}
				}
				
				// Check to see if opportunity to create a buy order now
				if(total_market_buy < 2){
					decimal wallet_value = base_price * base_balance;
					decimal target_price;
					if(symbols[1] == "NDEX"){
						wallet_value = maximumNDEXBid * base_balance;
					}					
					if(wallet_value * maxWalletUtilize > minTradeAmountUSD){
						// Ok to buy with
						if(current_spread > mk.desiredSpread){
							// We do not want to narrow the spread so we put our order in the middle if possible
							if(bid_price_max == bid_price_min){
								// Set our target price slightly below
								target_price = bid_price_min - bid_price_min * 0.25m;
							}else{
								// Put our order in the middle
								target_price = (bid_price_max + bid_price_min) / 2.0m;
							}
						}else if(current_spread <= mk.desiredSpread && current_spread >= 0){
							// Put the order at our requested spread
							target_price = ask_price_min - ask_price_min * mk.desiredSpread;
						}else{
							// No spread available
							target_price = Math.Round(trade_price / base_price,8);
							if(symbols[0] == "NDEX"){
								// NDEX has a minimum price in price discovery phase
								target_price = Math.Round(maximumNDEXBid / base_price,8);
							}else{
								target_price = target_price - target_price * (mk.desiredSpread / 2);
							}							
						}
						target_price = Math.Round(target_price,8);
						decimal target_base_amount =base_balance * maxWalletUtilize;
						decimal target_amount = Math.Round(target_base_amount / target_price,8);
						decimal target_min_amount = target_amount * minAmountPercent;						
						if(symbols[0] == "NDEX"){
							// NDEX is NTP1 so indivisible
							target_amount = Math.Floor(target_amount);
							target_min_amount = Math.Floor(target_min_amount);
						}						
						bool good = await Task.Run(() => PostMakerOrder(0,target_price,target_amount,target_min_amount));
						if(good == false){
							LiquidityBotLog("Failed to post buy maker order to market");
							return;
						}
						if(total_market_buy == 0){
							// Post a second order slightly lower in price and lower in amount
							target_price = Math.Round(target_price - target_price * 0.5m,8);
							target_amount = target_amount - target_amount * 0.5m;
							target_min_amount = target_amount * minAmountPercent;
							if(symbols[0] == "NDEX"){
								// NDEX is NTP1 so it is indivisible
								target_amount = Math.Floor(target_amount);
								target_min_amount = Math.Floor(target_min_amount);
							}
							good = await Task.Run(() => PostMakerOrder(0,target_price,target_amount,target_min_amount));
							if(good == false){
								LiquidityBotLog("Failed to post second buy maker order to market");
								return;
							}							
						}
					}
				}
				
			}catch(Exception e){
				LiquidityBotLog("Unexpected program error: "+e.ToString());
			}finally{
				LiquidityBotLog("Finished checking market");
				int diff_time = UTCTime() - function_time; //The time this function took in seconds
				int sec_remain = 60 - diff_time;
				if(sec_remain < 0){sec_remain = 0;}
				PeriodicTimer.Change(sec_remain*1000,System.Threading.Timeout.Infinite); //Run again soon
				currentMarket++;
				if(currentMarket >= marketList.Count){
					currentMarket = 0;
				}
			}
		}
		
		public static bool ChangeMarket(string market)
		{
			// This returns true if success code received
			string rawdata = HttpRequest(traderAPIEndpoint,"request=changeMarket&desiredMarket="+market,false,3);				
			if(rawdata.Length > 0){
				JObject js = JObject.Parse(rawdata);
				if(Convert.ToInt32(js["code"].ToString()) == 1){ // 1 is the success code
					return true;
				}
			}
			return false;			
		}
		
		public static bool PostMakerOrder(int type, decimal price, decimal amount, decimal min_amount)
		{			
			string order_type = "SELL";
			if(type == 0){				
				order_type = "BUY";
			}
			string price_string = String.Format(CultureInfo.InvariantCulture,"{0:0.########}",price);
			string amount_string = String.Format(CultureInfo.InvariantCulture,"{0:0.########}",amount);
			string min_amount_string = String.Format(CultureInfo.InvariantCulture,"{0:0.########}",min_amount);
			bool waiting_erc20 = false;
			for(int i = 0;i < 5;i++){
				string rawdata = HttpRequest(traderAPIEndpoint,"request=postMakerOrder&orderType="+order_type+"&price="+price_string+"&amount="+amount_string+"&minAmount="+min_amount_string+"&approveERC20=true",false,10);				
				if(rawdata.Length > 0){
					JObject js = JObject.Parse(rawdata);
					int code = Convert.ToInt32(js["code"].ToString());
					if(code == 1){ // 1 is the success code
						return true;
					}else if(code == 13){
						// Code 13 means the approval transaction was sent to the ERC20 token contract, so we must wait for that transaction to become approved
						waiting_erc20 = true;
					}
					string req = "request=postMakerOrder&orderType="+order_type+"&price="+price_string+"&amount="+amount_string+"&minAmount="+min_amount_string+"&approveERC20=true";
					LiquidityBotLog("Post failure request: " + req + ", resp" + rawdata);
				}
				if(waiting_erc20 == false){
					// Transaction failed, return false
					break;
				}
				Thread.Sleep(30*1000); // Wait 30 seconds before trying to post again
			}
			return false;
		}
		
		public static bool CloseMyOrder(string id)
		{
			// This returns true if success code received
			string rawdata = HttpRequest(traderAPIEndpoint,"request=cancelOrder&orderID="+id,false,3);				
			if(rawdata.Length > 0){
				JObject js = JObject.Parse(rawdata);
				if(Convert.ToInt32(js["code"].ToString()) == 1){ // 1 is the success code
					return true;
				}
			}
			return false;			
		}
		
		public static string GetCurrentMarket()
		{
			string rawdata = HttpRequest(traderAPIEndpoint,"request=currentMarket",false,3);				
			if(rawdata.Length > 0){
				JObject js = JObject.Parse(rawdata);
				return js["result"].ToString(); // Will be 'Loading Market' is not yet ready
			}
			return "";			
		}
		
		public static decimal GetWalletBalance(string symbol)
		{
			string rawdata = HttpRequest(traderAPIEndpoint,"request=walletDetails&coin="+symbol,false,3);				
			if(rawdata.Length > 0){
				JObject js = JObject.Parse(rawdata);
				if(js["result"]["balance"] != null){
					if(js["result"]["status"].ToString() == "Not Available"){
						// Wallet is currently not available, register it as zero balance
						return 0;
					}
					return decimal.Parse(js["result"]["balance"].ToString(),CultureInfo.InvariantCulture);
				}else{
					return 0;
				}
			}
			return -1;			
		}
		
		public static decimal GetUSDPrice(string symbol_id)
		{
			string rawdata = HttpRequest(coinGeckoEndpoint,"ids="+symbol_id+"&vs_currencies=usd",false,3);				
			if(rawdata.Length > 0){
				JObject js = JObject.Parse(rawdata);
				return decimal.Parse(js[symbol_id]["usd"].ToString(),CultureInfo.InvariantCulture);
			}
			return -1;			
		}
		
		public static string HttpRequest(string url, string rdata, bool postrequest, int timeout_sec)
		{
			int request_time = UTCTime();
			string responseString = "";
			bool timeout = false;
			while(UTCTime() - request_time < timeout_sec){
				timeout = false;
				try {
					//This will make a request and return the result
					//Post data must be formatted: var=something&var2=something
					
					if(postrequest == false){
						url = url+"?"+rdata; //Put the URL data in the getrequest
					}
					
					//This prevents server changes from breaking the protocol
					ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
					
					HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
					request.Credentials = System.Net.CredentialCache.DefaultCredentials;
					request.Timeout = timeout_sec*1000; //Wait variable seconds
					
					if(rdata.Length > 0 && postrequest == true){ //It is a post request
						byte[] data = Encoding.ASCII.GetBytes(rdata);
						
						request.Method = "POST";
						request.ContentType = "application/json";
						request.ContentLength = data.Length;
						
						using (var stream = request.GetRequestStream())
						{
						    stream.Write(data, 0, data.Length); //Write post data
						}				
					}
					
					HttpWebResponse response = (HttpWebResponse)request.GetResponse();
					
					StreamReader respreader = new StreamReader(response.GetResponseStream());
					responseString = respreader.ReadToEnd();	
					respreader.Close();
					response.Close();
					break; //Leave the while loop, we got a good response
				} catch (WebException e){
					if(e.Status == WebExceptionStatus.Timeout){
						//The server is offline or computer is not connected to internet
						timeout = true;
						LiquidityBotLog("Timeout error");
					}else{
						//Get the error
						if(e.Response != null){
							StreamReader respreader = new StreamReader(e.Response.GetResponseStream());
							string err_string = respreader.ReadToEnd();
							respreader.Close();
							e.Response.Close();
							LiquidityBotLog("Request Error: "+err_string);
						}else{
							timeout = true;
							LiquidityBotLog("Connection Error: "+e.ToString());
						}
					}
				} catch (Exception e) {
					LiquidityBotLog("Unexpected Error: "+e.ToString());
				}
				
				if(timeout == true){
					break; //We won't retry because timeout was reached
				}
			}
			return responseString;
		}
		
		private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0,
		                                                          DateTimeKind.Utc);
		
		//Converts UTC seconds to DateTime object
		public static DateTime UTC2DateTime(int utc_seconds)
		{
		    return UnixEpoch.AddSeconds(utc_seconds);
		}
		
		public static int UTCTime()
		{
			//Returns time since epoch
			TimeSpan t = DateTime.UtcNow - UnixEpoch;
			return (int)t.TotalSeconds;
		}
		
		public static System.Object debugfileLock = new System.Object(); 
		public static void LiquidityBotLog(string msg)
		{
			System.Diagnostics.Debug.WriteLine(msg);
			//Also write this information to a log
			lock(debugfileLock){
				try {
					using (StreamWriter file = File.AppendText("debug.log"))
					{
						string format_time = UTC2DateTime(UTCTime()).ToString("MM-dd:HH-mm-ss");
					  	file.WriteLine(format_time+": "+msg);
					}			
				} catch (Exception) { }
			}
		}
	}
}