﻿using System.Windows.Input;
using CryptoToolkit;
using GigMobile.Models;
using GigMobile.Services;

namespace GigMobile.ViewModels.TrustEnforcers
{
    public class VerifyNumberViewModel : BaseViewModel<TrustEnforcer>
    {
        private readonly GigGossipNode _gigGossipNode;
        private readonly ISecureDatabase _secureDatabase;
        private TrustEnforcer _newTrustEnforcer;

        private ICommand _submitCommand;
        public ICommand SubmitCommand => _submitCommand ??= new Command(async () => await SubmitAsync());

        public short? Code0 { get; set; }
        public short? Code1 { get; set; }
        public short? Code2 { get; set; }
        public short? Code3 { get; set; }
        public short? Code4 { get; set; }
        public short? Code5 { get; set; }

        public override void Prepare(TrustEnforcer data)
        {
            _newTrustEnforcer = data;
        }

        public VerifyNumberViewModel(GigGossipNode gigGossipNode, ISecureDatabase secureDatabase)
        {
            _gigGossipNode = gigGossipNode;
            _secureDatabase = secureDatabase;
        }

        public async override Task Initialize()
        {
            await base.Initialize();

            try
            {
                var token = await _gigGossipNode.MakeSettlerAuthTokenAsync(new Uri(_newTrustEnforcer.Uri));
                var settlerClient = _gigGossipNode.SettlerSelector.GetSettlerClient(new Uri(_newTrustEnforcer.Uri));
                await settlerClient.VerifyChannelAsync(token, _gigGossipNode.PublicKey, "PhoneNumber", "SMS", _newTrustEnforcer.PhoneNumber);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }


        private async Task SubmitAsync()
        {
            try
            {
                var token = await _gigGossipNode.MakeSettlerAuthTokenAsync(new Uri(_newTrustEnforcer.Uri));
                var settlerClient = _gigGossipNode.SettlerSelector.GetSettlerClient(new Uri(_newTrustEnforcer.Uri));
                var secret = $"{Code0}{Code1}{Code2}{Code3}{Code4}{Code5}";
                var retries = await settlerClient.SubmitChannelSecretAsync(token, _gigGossipNode.PublicKey, "PhoneNumber", "SMS", _newTrustEnforcer.PhoneNumber, secret);
                if (retries == -1)//code was ok
                {
                    var settletCert = await settlerClient.IssueCertificateAsync(token, _gigGossipNode.PublicKey, new List<string> { "PhoneNumber" });
                    _newTrustEnforcer.Certificate = Crypto.DeserializeObject<Certificate>(settletCert);
                    await _secureDatabase.CreateOrUpdateTrustEnforcersAsync(_newTrustEnforcer);
                    await NavigationService.NavigateBackAsync();
                }
                else if (retries > 0)
                {
                    //retry?   
                }
                else
                {
                    await Application.Current.MainPage.DisplayAlert("Verefication faild", "Please verify your number", "Cancel");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}

