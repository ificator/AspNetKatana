﻿// <copyright file="MicrosoftAccountAuthenticationHandler.cs" company="Microsoft Open Technologies, Inc.">
// Copyright 2011-2013 Microsoft Open Technologies, Inc. All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.Owin.Logging;
using Microsoft.Owin.Security.Infrastructure;

using Newtonsoft.Json.Linq;

namespace Microsoft.Owin.Security.MicrosoftAccount
{
    internal class MicrosoftAccountAuthenticationHandler : AuthenticationHandler<MicrosoftAccountAuthenticationOptions>
    {
        private const string TokenEndpoint = "https://login.live.com/oauth20_token.srf";
        private const string GraphApiEndpoint = "https://apis.live.net/v5.0/me";

        private readonly ILogger _logger;

        public MicrosoftAccountAuthenticationHandler(ILogger logger)
        {
            _logger = logger;
        }

        public override async Task<bool> Invoke()
        {
            if (Options.ReturnEndpointPath != null &&
                String.Equals(Options.ReturnEndpointPath, Request.Path, StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeReturnPath();
            }
            return false;
        }

        protected override async Task<AuthenticationTicket> AuthenticateCore()
        {
            _logger.WriteVerbose("AuthenticateCore");

            AuthenticationExtra extra = null;
            try
            {
                string code = null;
                string state = null;

                IDictionary<string, string[]> query = Request.GetQuery();
                string[] values;
                if (query.TryGetValue("code", out values) && values != null && values.Length == 1)
                {
                    code = values[0];
                }
                if (query.TryGetValue("state", out values) && values != null && values.Length == 1)
                {
                    state = values[0];
                }

                extra = Options.StateDataHandler.Unprotect(state);
                if (extra == null)
                {
                    return null;
                }

                // OAuth2 10.12 CSRF
                if (!ValidateCorrelationId(extra, _logger))
                {
                    return new AuthenticationTicket(null, extra);
                }

                var tokenRequestParameters = string.Format(
                    CultureInfo.InvariantCulture,
                    "client_id={0}&redirect_uri={1}&client_secret={2}&code={3}&grant_type=authorization_code",
                    Uri.EscapeDataString(Options.ClientId),
                    Uri.EscapeDataString(GenerateRedirectUri()),
                    Uri.EscapeDataString(Options.ClientSecret),
                    code);

                WebRequest tokenRequest = WebRequest.Create(TokenEndpoint);
                tokenRequest.Method = "POST";
                tokenRequest.ContentType = "application/x-www-form-urlencoded";
                tokenRequest.ContentLength = tokenRequestParameters.Length;
                tokenRequest.Timeout = Options.BackChannelRequestTimeOut;
                using (var bodyStream = new StreamWriter(tokenRequest.GetRequestStream()))
                {
                    bodyStream.Write(tokenRequestParameters);
                }

                WebResponse tokenResponse = await tokenRequest.GetResponseAsync();
                string accessToken = null;

                using (var reader = new StreamReader(tokenResponse.GetResponseStream()))
                {
                    string oauthTokenResponse = await reader.ReadToEndAsync();
                    JObject oauth2Token = JObject.Parse(oauthTokenResponse);
                    accessToken = oauth2Token["access_token"].Value<string>();
                }

                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    _logger.WriteWarning("Access token was not found");
                    return new AuthenticationTicket(null, extra);
                }

                JObject accountInformation;
                var accountInformationRequest = WebRequest.Create(GraphApiEndpoint + "?access_token=" + Uri.EscapeDataString(accessToken));
                accountInformationRequest.Timeout = Options.BackChannelRequestTimeOut;
                var accountInformationResponse = await accountInformationRequest.GetResponseAsync();
                using (var reader = new StreamReader(accountInformationResponse.GetResponseStream()))
                {
                    accountInformation = JObject.Parse(await reader.ReadToEndAsync());
                }

                var context = new MicrosoftAccountAuthenticatedContext(Request.Environment, accountInformation, accessToken);
                context.Identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, context.Id, "http://www.w3.org/2001/XMLSchema#string", Options.AuthenticationType),
                        new Claim(ClaimTypes.Name, context.Name, "http://www.w3.org/2001/XMLSchema#string", Options.AuthenticationType),
                        new Claim("urn:microsoftaccount:id", context.Id, "http://www.w3.org/2001/XMLSchema#string", Options.AuthenticationType),
                        new Claim("urn:microsoftaccount:name", context.Name, "http://www.w3.org/2001/XMLSchema#string", Options.AuthenticationType),
                    },
                    Options.AuthenticationType,
                    ClaimsIdentity.DefaultNameClaimType,
                    ClaimsIdentity.DefaultRoleClaimType);
                if (!string.IsNullOrWhiteSpace(context.Email))
                {
                    context.Identity.AddClaim(new Claim(ClaimTypes.Email, context.Email, "http://www.w3.org/2001/XMLSchema#string", Options.AuthenticationType));
                }

                await Options.Provider.Authenticated(context);

                context.Extra = extra;

                return new AuthenticationTicket(context.Identity, context.Extra);
            }
            catch (Exception ex)
            {
                _logger.WriteWarning("Authentication failed", ex);
                return new AuthenticationTicket(null, extra);
            }
        }

        protected override async Task ApplyResponseChallenge()
        {
            _logger.WriteVerbose("ApplyResponseChallenge");

            if (Response.StatusCode != 401)
            {
                return;
            }

            var challenge = Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);

            if (challenge != null)
            {
                string requestPrefix = Request.Scheme + "://" + Request.Host;
                string currentQueryString = Request.QueryString;
                string currentUri = string.IsNullOrEmpty(currentQueryString)
                    ? requestPrefix + Request.PathBase + Request.Path
                    : requestPrefix + Request.PathBase + Request.Path + "?" + currentQueryString;

                string redirectUri = requestPrefix + Request.PathBase + Options.ReturnEndpointPath;

                var extra = challenge.Extra;
                if (string.IsNullOrEmpty(extra.RedirectUrl))
                {
                    extra.RedirectUrl = currentUri;
                }

                // OAuth2 10.12 CSRF
                GenerateCorrelationId(extra);

                string scope = this.Options.Scope.Aggregate(string.Empty, (current, scopeEntry) => current + (scopeEntry + " ")).TrimEnd(' ');

                string state = Options.StateDataHandler.Protect(extra);

                string authorizationEndpoint =
                    "https://login.live.com/oauth20_authorize.srf" +
                        "?client_id=" + Uri.EscapeDataString(Options.ClientId) +
                        "&scope=" + Uri.EscapeDataString(scope) +
                        "&response_type=code" +
                        "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                        "&state=" + Uri.EscapeDataString(state);

                Response.StatusCode = 302;
                Response.SetHeader("Location", authorizationEndpoint);
            }
        }

        public async Task<bool> InvokeReturnPath()
        {
            _logger.WriteVerbose("InvokeReturnPath");

            var model = await Authenticate();

            var context = new MicrosoftAccountReturnEndpointContext(Request.Environment, model, ErrorDetails);
            context.SignInAsAuthenticationType = Options.SignInAsAuthenticationType;
            context.RedirectUri = model.Extra.RedirectUrl;
            model.Extra.RedirectUrl = null;

            await Options.Provider.ReturnEndpoint(context);

            if (context.SignInAsAuthenticationType != null && context.Identity != null)
            {
                ClaimsIdentity signInIdentity = context.Identity;
                if (!string.Equals(signInIdentity.AuthenticationType, context.SignInAsAuthenticationType, StringComparison.Ordinal))
                {
                    signInIdentity = new ClaimsIdentity(signInIdentity.Claims, context.SignInAsAuthenticationType, signInIdentity.NameClaimType, signInIdentity.RoleClaimType);
                }
                Response.Grant(signInIdentity, context.Extra);
            }

            if (!context.IsRequestCompleted && context.RedirectUri != null)
            {
                Response.Redirect(context.RedirectUri);
                context.RequestCompleted();
            }

            return context.IsRequestCompleted;
        }

        private string GenerateRedirectUri()
        {
            string requestPrefix = Request.Scheme + "://" + Request.Host;

            string redirectUri = requestPrefix + RequestPathBase + Options.ReturnEndpointPath; // + "?state=" + Uri.EscapeDataString(Options.StateDataHandler.Protect(state));            
            return redirectUri;
        }
    }
}