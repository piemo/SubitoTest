﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.ModelBinding;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.EntityFramework;
using Microsoft.AspNet.Identity.Owin;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OAuth;
using SubitoNotifier.Models;
using SubitoNotifier.Providers;
using SubitoNotifier.Results;
using Newtonsoft.Json;
using SubitoNotifier.Helper;
using System.Linq;
using System.Net;
using System.IO;
using System.Text;
using System.IO.Compression;
using System.Drawing;

namespace SubitoNotifier.Controllers
{
    [RoutePrefix("api/Subito")]
    public class SubitoController : ApiController
    {
        string URL = "https://hades.subito.it/v1";  //url base subito per richieste senza cookies
        string COOKIESURL = "https://ade.subito.it/v1"; // url base subito per richieste con cookies

        private const string LocalLoginProvider = "Local";
        private ApplicationUserManager _userManager;
        string maxNum;          //quantità massima di inserzioni restituite
        string pin;             //da capire
        string searchText;      //stringa ricercata
        string sort;            //ordinamento risultati. Impostato su data decrescente
        string typeIns;         //da utilizzare per gli immobili. s= in vendita, u= in affitto, h= in affitto per vacanze, oppure "s,u,h" per tutte le inserzioni
        string category;        //2 auto,3 moto e scooter,4 veicoli commerciali,5 accessori auto,7 appartamenti,8 Uffici e Locali commerciali,44 Console e Videogiochi,10 Informatica,11 Audio/Video,12 telefonia
                                //14 Arredamento e Casalinghi,15 Giardino e Fai da te,16 Abbigliamento e Accessori,17 Tutto per i bambini,23 Animali,24 Candidati in cerca di lavoro,25 Attrezzature di lavoro
                                //26 Offerte di lavoro,28 Altri,29 Ville singole e a schiera,30 Terreni e rustici,31 Garage e box,32 Loft mansarde e altro,33 Case vacanza,34 Caravan e Camper,36 Accessori Moto,
                                //37 Elettrodomestici,38 Libri e Riviste,39 Strumenti Musicali,40 fotografia,41 biciclette, 
        string city;            //città. codici da estrapolare 
        string region;          //regione. codice da estrapolare al momento


        public SubitoController()
        {
            this.maxNum = Uri.EscapeDataString("20");
            this.pin = "0,0,0";
            this.sort = "datedesc";
            this.typeIns = "s,u,h";
        }

        [Route("GetLatestNewInsertion")]
        public async Task<string> GetInsertion(string botToken, string chatToken, string category="", string city ="", string region ="", string searchText="")
        {
            try
            {
                this.searchText = Uri.EscapeDataString(searchText);
                this.category = Uri.EscapeDataString(category.ToString());
                this.city = Uri.EscapeDataString(city.ToString());
                this.region = Uri.EscapeDataString(region.ToString());
                string parameter = $"/search/ads?lim={this.maxNum}&pin={this.pin}&sort={this.sort}&t={this.typeIns}";

                if (this.category != "")
                    parameter += $"&c={this.category}";

                if (this.city != "")
                    parameter += $"&ci={this.city}";

                if (this.region != "")
                    parameter += $"&r={this.region}";

                if (this.searchText != "")
                    parameter += $"&q={this.searchText}";

                SubitoWebClient webClient = new SubitoWebClient();
                string subitoResponse = await webClient.DownloadStringTaskAsync(new Uri(URL + parameter, UriKind.Absolute));
                var insertions = JsonConvert.DeserializeObject<Insertions>(subitoResponse);
                if(insertions.ads.Count>0)
                {
                    List<Ad> newAds = new List<Ad>();
                    var firstId = insertions.GetFirstAdId();
                    var latestInsertion = SQLHelper.GetLatestInsertionID(this.searchText);
                    if (latestInsertion == null)
                    {
                        newAds.Add(insertions.ads.FirstOrDefault());
                        SQLHelper.InsertLatestInsertion(firstId, this.searchText);
                    }
                    else if (firstId > latestInsertion.SubitoId)
                    {
                        for (int i = 0; i < insertions.ads.Count() && SubitoHelper.GetAdId(insertions.ads[i]) > latestInsertion.SubitoId; i++)
                        {
                            newAds.Add(insertions.ads.ElementAt(i));
                        }
                        latestInsertion.SubitoId = firstId;
                        SQLHelper.UpdateLatestInsertion(latestInsertion);
                    }

                    foreach(Ad ad in newAds)
                    {
                        await SubitoHelper.sendTelegramInsertion(botToken, $"-{chatToken}", this.searchText, ad);
                    }
                }
                return $"Controllato {DateTime.Now}";
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }

        [Route("GetDeleteAll")]
        public async Task<string> GetDeleteAll(string username , string password)
        {
            try
            {
                SubitoWebClient subitoWebClient = new SubitoWebClient();
                //login to get cookies
                SubitoLoginDetail loginData = await LoginSubito(username, password, subitoWebClient);

                //getting the list of own insertions
                Insertions insertions = await GetUserInsertionsByID(loginData.user_id, subitoWebClient);

                //deleting insertions
                foreach (Ad ad in insertions.ads)
                {
                    Uri uri = new Uri(COOKIESURL + "/users/" + loginData.user_id + "/ads/" + ad.urn + "?delete_reason=sold_on_subito");
                    bool result = await subitoWebClient.DeleteRequest(uri);
                    await Task.Delay(1000);
                }

                return $"inserzioni rimosse {DateTime.Now}";
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }

        [Route("GetReinsertAll")]
        public async Task<string> GetReinsertAll(string username, string password , string addressNewInsertions )
        {
            try
            {
                SubitoWebClient subitoWebClient = new SubitoWebClient();
                
                //login to get cookies
                SubitoLoginDetail loginData = await LoginSubito(username,password,subitoWebClient);

                // Getting the list of insertions to post from a json on pastebin.com
                List<NewInsertion> newInsertions = new List<NewInsertion>();
                string responseString = await subitoWebClient.DownloadStringTaskAsync(new Uri("http://pastebin.com/raw/"+ addressNewInsertions));
                newInsertions = JsonConvert.DeserializeObject<List<NewInsertion>>(responseString);

                foreach(NewInsertion ins in newInsertions)
                {
                    string result = await PostNewInsertion(ins, subitoWebClient);
                }

                return $"inserzioni aggiunte {DateTime.Now}";
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }

        public async Task<string> PostNewInsertion(NewInsertion newInsertion, SubitoWebClient subitoWebClient)
        {
            //calling the webservices to initiate the request of a new insertion.
            string response = await subitoWebClient.GetRequest(new Uri("https://api2.subito.it:8443/api/v5/aij/form/0?v=5", UriKind.Absolute));
            response = await subitoWebClient.GetRequest(new Uri("https://api2.subito.it:8443/aij/init/0?v=5&v=5", UriKind.Absolute));
            response = await subitoWebClient.GetRequest(new Uri("https://api2.subito.it:8443/aij/load/0?v=5&v=5", UriKind.Absolute));
            response = await subitoWebClient.GetRequest(new Uri("https://api2.subito.it:8443/aij/form/0?v=5&v=5", UriKind.Absolute));
            //check
            response = await subitoWebClient.PostRequest(newInsertion.ToString(), new Uri("https://api2.subito.it:8443/api/v5/aij/verify/0", UriKind.Absolute));

            //inserimento 
            foreach(string imageAddress in newInsertion.images)
            {
                string imageToString = Convert.ToBase64String(subitoWebClient.DownloadData(new Uri(imageAddress)));
                response = await subitoWebClient.PostImageRequest(imageToString, newInsertion.Category, new Uri("https://api2.subito.it:8443/api/v5/aij/addimage/0", UriKind.Absolute));
                response = await subitoWebClient.GetRequest(new Uri("https://api2.subito.it:8443/aij/addimage_form/0?v=5&category="+ newInsertion.Category, UriKind.Absolute));
            }

            //inserito
            response = await subitoWebClient.PostRequest(newInsertion.ToString(), new Uri("https://api2.subito.it:8443/api/v5/aij/create/0", UriKind.Absolute));
            return response;
        }

        public async Task<Insertions> GetUserInsertionsByID(int id, SubitoWebClient subitoWebClient)
        {
            Uri uri = new Uri(COOKIESURL + "/users/" + id + "/ads?start=0");
            string responseString = await subitoWebClient.DownloadStringTaskAsync(uri);
            return JsonConvert.DeserializeObject<Insertions>(responseString);
        }

        public async Task<SubitoLoginDetail> LoginSubito(string username, string password, SubitoWebClient webClient)
        {
            Uri uri=  new Uri(COOKIESURL + "/users/login");
            string loginString = "{ \"password\":\"" + password + "\",\"remember_me\":true,\"username\":\"" + username + "\"}";
            string responseString = await webClient.getLoginResponse(loginString, uri);
            return JsonConvert.DeserializeObject<SubitoLoginDetail>(responseString);
        }

    }

}

