﻿using Kentor.AuthServices.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.IdentityModel.Tokens;
using System.Net;
using System.IdentityModel.Metadata;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Configuration;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;

namespace Kentor.AuthServices
{
    /// <summary>
    /// Represents a known identity provider that this service provider can communicate with.
    /// </summary>
    public class IdentityProvider
    {
        private static readonly IDictionary<EntityId, IdentityProvider> configuredIdentityProviders =
            KentorAuthServicesSection.Current.IdentityProviders.ToDictionary(
                idp => new EntityId(idp.EntityId),
                idp => new IdentityProvider(idp),
                EntityIdEqualityComparer.Instance);

        public class ActiveIdentityProvidersMap : IEnumerable<IdentityProvider>
        {
            private readonly IDictionary<EntityId, IdentityProvider> configuredIdps;
            private readonly IList<Federation> configuredFederations;
            
            internal ActiveIdentityProvidersMap(
                IDictionary<EntityId, IdentityProvider> configuredIdps,
                IList<Federation> configuredFederations)
            {
                this.configuredIdps = configuredIdps;
                this.configuredFederations = configuredFederations;
            }

            public IdentityProvider this[EntityId entityId]
            {
                get
                {
                    IdentityProvider idp;
                    if (TryGetValue(entityId, out idp))
                    {
                        return idp;
                    }
                    else 
                    {
                        throw new KeyNotFoundException("No Idp with entity id \"" + entityId.Id + "\" found.");
                    }
                }
            }

            public bool TryGetValue(EntityId entityId, out IdentityProvider idp)
            {
                if(configuredIdps.TryGetValue(entityId, out idp))
                {
                    return true;
                }

                foreach(var federation in configuredFederations)
                {
                    if(federation.IdentityProviders.TryGetValue(entityId, out idp))
                    {
                        return true;
                    }
                }

                return false;
            }

            public IEnumerator<IdentityProvider> GetEnumerator()
            {
                return configuredIdps.Values.Union(
                configuredFederations.SelectMany(f => f.IdentityProviders.Select(i => i.Value)))
                .GetEnumerator();
            }

            [ExcludeFromCodeCoverage]
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }
        }

        private static readonly ActiveIdentityProvidersMap activeIdentityProviders = 
            new ActiveIdentityProvidersMap(
                configuredIdentityProviders,
                KentorAuthServicesSection.Current.Federations.Select(
                f => new Federation(f.MetadataUrl, f.AllowUnsolicitedAuthnResponse)).ToList());

        [Obsolete]
        public static ActiveIdentityProvidersMap ActiveIdentityProviders
        {
            get
            {
                return activeIdentityProviders;
            }
        }

        // Ctor used for testing.
        internal IdentityProvider(Uri destinationUri)
        {
            SingleSignOnServiceUrl = destinationUri;
        }

        internal IdentityProvider(IdentityProviderElement config)
        {
            SingleSignOnServiceUrl = config.DestinationUri;
            EntityId = new EntityId(config.EntityId);
            Binding = config.Binding;
            AllowUnsolicitedAuthnResponse = config.AllowUnsolicitedAuthnResponse;

            var certificate = config.SigningCertificate.LoadCertificate();

            if (certificate != null)
            {
                SigningKey = certificate.PublicKey.Key;
            }

            if (config.LoadMetadata)
            {
                LoadMetadata();
            }

            Validate();
        }

        internal IdentityProvider(EntityDescriptor metadata, bool allowUnsolicitedAuthnResponse)
        {
            AllowUnsolicitedAuthnResponse = allowUnsolicitedAuthnResponse;

            LoadMetadata(metadata);

            Validate();
        }

        private void Validate()
        {
            if(Binding == 0)
            {
                throw new ConfigurationErrorsException("Missing binding configuration on Idp " + EntityId.Id + ".");
            }

            if(SigningKey == null)
            {
                throw new ConfigurationErrorsException("Missing signing certificate configuration on Idp " + EntityId.Id + ".");
            }

            if (SingleSignOnServiceUrl == null)
            {
                throw new ConfigurationErrorsException("Missing assertion consumer service url configuration on Idp " + EntityId.Id + ".");
            }
        }

        /// <summary>
        /// The binding used when sending AuthnRequests to the identity provider.
        /// </summary>
        public Saml2BindingType Binding { get; private set; }

        /// <summary>
        /// The Url of the single sign on service. This is where the browser is redirected or
        /// where the post data is sent to when sending an AuthnRequest to the idp.
        /// </summary>
        public Uri SingleSignOnServiceUrl { get; private set; }

        /// <summary>
        /// The Entity Id of the identity provider.
        /// </summary>
        public EntityId EntityId { get; private set; }

        /// <summary>
        /// Is this idp allowed to send unsolicited responses, i.e. idp initiated sign in?
        /// </summary>
        public bool AllowUnsolicitedAuthnResponse { get; private set; }

        /// <summary>
        /// Create an authenticate request aimed for this idp.
        /// </summary>
        /// <param name="returnUrl">The return url where the browser should be sent after
        /// successful authentication.</param>
        /// <returns></returns>
        public Saml2AuthenticationRequest CreateAuthenticateRequest(Uri returnUrl)
        {
            var request = new Saml2AuthenticationRequest()
            {
                DestinationUri = SingleSignOnServiceUrl,
                AssertionConsumerServiceUrl = KentorAuthServicesSection.Current.AssertionConsumerServiceUrl,
                Issuer = KentorAuthServicesSection.Current.EntityId
            };

            var responseData = new StoredRequestState(EntityId, returnUrl);

            PendingAuthnRequests.Add(new Saml2Id(request.Id), responseData);

            return request;
        }

        /// <summary>
        /// Bind a Saml2AuthenticateRequest using the active binding of the idp,
        /// producing a CommandResult with the result of the binding.
        /// </summary>
        /// <param name="request">The AuthnRequest to bind.</param>
        /// <returns>CommandResult with the bound request.</returns>
        public CommandResult Bind(Saml2AuthenticationRequest request)
        {
            return Saml2Binding.Get(Binding).Bind(request);
        }

        /// <summary>
        /// The public key of the idp that is used to verify signatures of responses/assertions.
        /// </summary>
        public AsymmetricAlgorithm SigningKey { get; private set; }

        private void LoadMetadata()
        {
            // So far only support for metadata at well known location.
            var metadata = MetadataLoader.LoadIdp(new Uri(EntityId.Id));

            LoadMetadata(metadata);
        }

        private void LoadMetadata(EntityDescriptor metadata)
        {
            if (EntityId != null)
            {
                if (metadata.EntityId.Id != EntityId.Id)
                {
                    var msg = string.Format(CultureInfo.InvariantCulture,
                        "Unexpected entity id \"{0}\" found when loading metadata for \"{1}\".",
                        metadata.EntityId.Id, EntityId.Id);
                    throw new ConfigurationErrorsException(msg);
                }
            }
            else
            {
                EntityId = metadata.EntityId;
            }

            var idpDescriptor = metadata.RoleDescriptors
                .OfType<IdentityProviderSingleSignOnDescriptor>().Single();

            // Prefer an endpoint with a redirect binding, then check for POST which 
            // is the other supported by AuthServices.
            var ssoService = idpDescriptor.SingleSignOnServices
                .FirstOrDefault(s => s.Binding == Saml2Binding.HttpRedirectUri) ??
                idpDescriptor.SingleSignOnServices
                .First(s => s.Binding == Saml2Binding.HttpPostUri);

            Binding = Saml2Binding.UriToSaml2BindingType(ssoService.Binding);
            SingleSignOnServiceUrl = ssoService.Location;

            var key = idpDescriptor.Keys
                .Where(k => k.Use == KeyType.Unspecified || k.Use == KeyType.Signing)
                .SingleOrDefault();

            if (key != null)
            {
                SigningKey = ((AsymmetricSecurityKey)key.KeyInfo.CreateKey())
                    .GetAsymmetricAlgorithm(SignedXml.XmlDsigRSASHA1Url, false);
            }
        }
    }
}
