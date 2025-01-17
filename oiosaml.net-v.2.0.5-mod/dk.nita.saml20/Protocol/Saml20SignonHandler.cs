using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Web;
using System.Web.Caching;
using System.Xml;
using dk.nita.saml20.Actions;
using dk.nita.saml20.Bindings;
using dk.nita.saml20.Bindings.SignatureProviders;
using dk.nita.saml20.Profiles.DKSaml20.Attributes;
using dk.nita.saml20.Session;
using dk.nita.saml20.session;
using dk.nita.saml20.config;
using dk.nita.saml20.Identity;
using dk.nita.saml20.Logging;
using dk.nita.saml20.Properties;
using dk.nita.saml20.protocol.pages;
using dk.nita.saml20.Schema.Core;
using dk.nita.saml20.Schema.Metadata;
using dk.nita.saml20.Schema.Protocol;
using dk.nita.saml20.Specification;
using dk.nita.saml20.Utils;
using Saml2.Properties;
using Trace=dk.nita.saml20.Utils.Trace;

namespace dk.nita.saml20.protocol
{
    /// <summary>
    /// Implements a Saml 2.0 protocol sign-on endpoint. Handles all SAML bindings.
    /// </summary>
    public class Saml20SignonHandler : Saml20AbstractEndpointHandler
    {
        private readonly X509Certificate2  _certificate;

        /// <summary>
        /// Initializes a new instance of the <see cref="Saml20SignonHandler"/> class.
        /// </summary>
        public Saml20SignonHandler()
        {
            _certificate = FederationConfig.GetConfig().SigningCertificate.GetCertificate();

            // Read the proper redirect url from config
            try
            {
                RedirectUrl = SAML20FederationConfig.GetConfig().ServiceProvider.SignOnEndpoint.RedirectUrl;
                ErrorBehaviour = SAML20FederationConfig.GetConfig().ServiceProvider.SignOnEndpoint.ErrorBehaviour.ToString();
            }
            catch(Exception e)
            {
                if (Trace.ShouldTrace(TraceEventType.Error))
                    Trace.TraceData(TraceEventType.Error, e.ToString());
            }
        }

        #region IHttpHandler Members

        /// <summary>
        /// Handles a request.
        /// </summary>
        /// <param name="context">The context.</param>
        protected override void Handle(HttpContext context)
        {
            Trace.TraceMethodCalled(GetType(), "Handle()");

            //Some IdP's are known to fail to set an actual value in the SOAPAction header
            //so we just check for the existence of the header field.
            if (Array.Exists(context.Request.Headers.AllKeys, delegate(string s) { return s == SOAPConstants.SOAPAction; }))
            {
                SessionStore.AssertSessionExists();

                HandleSOAP(context, context.Request.InputStream);
                return;
            }

            if (!string.IsNullOrEmpty(context.Request.Params["SAMLart"]))
            {
                SessionStore.AssertSessionExists();

                HandleArtifact(context);
            }

            if (!string.IsNullOrEmpty(context.Request.Params["SamlResponse"]))
            {
                SessionStore.AssertSessionExists();

                HandleResponse(context);
            }
            else
            {
                if (SAML20FederationConfig.GetConfig().CommonDomain.Enabled && context.Request.QueryString["r"] == null
                    && context.Request.Params["cidp"] == null)
                {
                    AuditLogging.logEntry(Direction.OUT, Operation.DISCOVER, "Redirecting to Common Domain for IDP discovery");
                    context.Response.Redirect(SAML20FederationConfig.GetConfig().CommonDomain.LocalReaderEndpoint);
                }
                else
                {
                    AuditLogging.logEntry(Direction.IN, Operation.ACCESS,
                                                 "User accessing resource: " + context.Request.RawUrl +
                                                 " without authentication.");

                    SessionStore.CreateSessionIfNotExists();

                    SendRequest(context);
                }
            }
        }
                
        #endregion

        private void HandleArtifact(HttpContext context)
        {
            HttpArtifactBindingBuilder builder = new HttpArtifactBindingBuilder(context);
            Stream inputStream = builder.ResolveArtifact();
            HandleSOAP(context, inputStream);
        }

        private void HandleSOAP(HttpContext context, Stream inputStream)
        {
            Trace.TraceMethodCalled(GetType(), "HandleSOAP");
            HttpArtifactBindingParser parser = new HttpArtifactBindingParser(inputStream);
            HttpArtifactBindingBuilder builder = new HttpArtifactBindingBuilder(context);

            if (parser.IsArtifactResolve())
            {
                Trace.TraceData(TraceEventType.Information, Tracing.ArtifactResolveIn);

                IDPEndPoint idp = RetrieveIDPConfiguration(parser.Issuer);
                AuditLogging.IdpId = idp.Id;
                AuditLogging.AssertionId = parser.ArtifactResolve.ID;
                if (!parser.CheckSamlMessageSignature(idp.metadata.Keys))
                {
                    HandleError(context, "Invalid Saml message signature");
                    AuditLogging.logEntry(Direction.IN, Operation.ARTIFACTRESOLVE, "Could not verify signature", parser.SamlMessage);
                }
                builder.RespondToArtifactResolve(idp, parser.ArtifactResolve);
            }
            else if (parser.IsArtifactResponse())
            {
                Trace.TraceData(TraceEventType.Information, Tracing.ArtifactResponseIn);

                Status status = parser.ArtifactResponse.Status;
                if (status.StatusCode.Value != Saml20Constants.StatusCodes.Success)
                {
                    HandleError(context, status);
                    AuditLogging.logEntry(Direction.IN, Operation.ARTIFACTRESOLVE, string.Format("Illegal status for ArtifactResponse {0} expected 'Success', msg: {1}", status.StatusCode.Value, parser.SamlMessage));
                    return;
                }
                if(parser.ArtifactResponse.Any.LocalName == Response.ELEMENT_NAME)
                {
                    bool isEncrypted;
                    XmlElement assertion = GetAssertion(parser.ArtifactResponse.Any, out isEncrypted);
                    if (assertion == null)
                        HandleError(context, "Missing assertion");
                    if(isEncrypted)
                    {
                        HandleEncryptedAssertion(context, assertion);
                    }
                    else
                    {
                        HandleAssertion(context, assertion);
                    }

                }
                else
                {
                    AuditLogging.logEntry(Direction.IN, Operation.ARTIFACTRESOLVE, string.Format("Unsupported payload message in ArtifactResponse: {0}, msg: {1}", parser.ArtifactResponse.Any.LocalName, parser.SamlMessage));
                    HandleError(context,
                                string.Format("Unsupported payload message in ArtifactResponse: {0}",
                                              parser.ArtifactResponse.Any.LocalName));
                }
            }
            else
            {
                Status s = parser.GetStatus();
                if (s != null)
                {
                    HandleError(context, s);
                }
                else
                {
                    AuditLogging.logEntry(Direction.IN, Operation.ARTIFACTRESOLVE, string.Format("Unsupported SamlMessage element: {0}, msg: {1}", parser.SamlMessageName, parser.SamlMessage));
                    HandleError(context, string.Format("Unsupported SamlMessage element: {0}", parser.SamlMessageName));
                }
            }
        }

        /// <summary>
        /// Send an authentication request to the IDP.
        /// </summary>
        private void SendRequest(HttpContext context)
        {
            Trace.TraceMethodCalled(GetType(), "SendRequest()");

            // See if the "ReturnUrl" - parameter is set.
            string returnUrl = context.Request.QueryString["ReturnUrl"];
            // If PreventOpenRedirectAttack has been enabled ... the return URL is only set if the URL is local.
            if (!string.IsNullOrEmpty(returnUrl) && (!FederationConfig.GetConfig().PreventOpenRedirectAttack || IsLocalUrl(returnUrl)))
                SessionStore.CurrentSession[SessionConstants.RedirectUrl] = returnUrl;
            
            IDPEndPoint idpEndpoint = RetrieveIDP(context);

            if (idpEndpoint == null)
            {
                //Display a page to the user where she can pick the IDP
                SelectSaml20IDP page = new SelectSaml20IDP();
                page.ProcessRequest(context);
                return;
            }

            Saml20AuthnRequest authnRequest = Saml20AuthnRequest.GetDefault();
            TransferClient(idpEndpoint, authnRequest, context);            
        }

        /// <summary>
        /// This method is used for preventing open redirect attacks.
        /// </summary>
        /// <param name="url">URL that is checked for being local or not.</param>
        /// <returns>Returns true if URL is local. Empty or null strings are not considered as local URL's</returns>
        private bool IsLocalUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }
            else
            {
                return ((url[0] == '/' && (url.Length == 1 ||
                        (url[1] != '/' && url[1] != '\\'))) ||   // "/" or "/foo" but not "//" or "/\"
                        (url.Length > 1 &&
                         url[0] == '~' && url[1] == '/'));   // "~/" or "~/foo"
            }
        }

        private Status GetStatusElement(XmlDocument doc)
        {
            XmlElement statElem =
                (XmlElement)doc.GetElementsByTagName(Status.ELEMENT_NAME, Saml20Constants.PROTOCOL)[0];

            return Serialization.DeserializeFromXmlString<Status>(statElem.OuterXml);
        }

        internal static XmlElement GetAssertion(XmlElement el, out bool isEncrypted)
        {
            
            XmlNodeList encryptedList =
                el.GetElementsByTagName(EncryptedAssertion.ELEMENT_NAME, Saml20Constants.ASSERTION);

            if (encryptedList.Count == 1)
            {
                isEncrypted = true;
                return (XmlElement)encryptedList[0];
            }

            XmlNodeList assertionList =
                el.GetElementsByTagName(Assertion.ELEMENT_NAME, Saml20Constants.ASSERTION);

            if (assertionList.Count == 1)
            {
                isEncrypted = false;
                return (XmlElement)assertionList[0];
            }

            isEncrypted = false;
            return null;
        }

        /// <summary>
        /// Handle the authentication response from the IDP.
        /// </summary>        
        private void HandleResponse(HttpContext context)
        {
            Encoding defaultEncoding = Encoding.UTF8;
            XmlDocument doc = GetDecodedSamlResponse(context, defaultEncoding);

            AuditLogging.logEntry(Direction.IN, Operation.LOGIN, "Received SAMLResponse: " + doc.OuterXml);

            try
            {

                XmlAttribute inResponseToAttribute =
                    doc.DocumentElement.Attributes["InResponseTo"];

                if(inResponseToAttribute == null)
                    throw new Saml20Exception("Received a response message that did not contain an InResponseTo attribute");

                string inResponseTo = inResponseToAttribute.Value;

                CheckReplayAttack(context, inResponseTo);
                
                Status status = GetStatusElement(doc);

                if (status.StatusCode.Value != Saml20Constants.StatusCodes.Success)
                {
                    if (status.StatusCode.Value == Saml20Constants.StatusCodes.Responder && status.StatusCode.SubStatusCode != null && Saml20Constants.StatusCodes.NoPassive == status.StatusCode.SubStatusCode.Value)
                        HandleError(context, "IdP responded with statuscode NoPassive. A user cannot be signed in with the IsPassiveFlag set when the user does not have a session with the IdP.");

                    HandleError(context, status);
                    return;
                }

                // Determine whether the assertion should be decrypted before being validated.
            
                bool isEncrypted;
                XmlElement assertion = GetAssertion(doc.DocumentElement, out isEncrypted);
                if (isEncrypted)
                {
                    assertion = GetDecryptedAssertion(assertion).Assertion.DocumentElement;
                }

                // Check if an encoding-override exists for the IdP endpoint in question
                string issuer = GetIssuer(assertion);
                IDPEndPoint endpoint = RetrieveIDPConfiguration(issuer);
                if (!string.IsNullOrEmpty(endpoint.ResponseEncoding))
                {
                    Encoding encodingOverride = null;
                    try
                    {
                        encodingOverride = System.Text.Encoding.GetEncoding(endpoint.ResponseEncoding);
                    }
                    catch (ArgumentException ex)
                    {
                        HandleError(context, ex);
                        return;
                    }

                    if (encodingOverride.CodePage != defaultEncoding.CodePage)
                    {
                        XmlDocument doc1 = GetDecodedSamlResponse(context, encodingOverride);
                        assertion = GetAssertion(doc1.DocumentElement, out isEncrypted);
                    }
                }

                HandleAssertion(context, assertion);
                return;
            }
            catch (Exception e)
            {
                HandleError(context, e);
                return;
            }
        }

        private static void CheckReplayAttack(HttpContext context, string inResponseTo)
        {
            if (string.IsNullOrEmpty(inResponseTo))
                throw new Saml20Exception("Empty InResponseTo from IdP is not allowed.");

            var expectedInResponseToSessionState = SessionStore.CurrentSession[SessionConstants.ExpectedInResponseTo];
            SessionStore.CurrentSession[SessionConstants.ExpectedInResponseTo] = null; // Ensure that no more responses can be received.

            string expectedInResponseTo = expectedInResponseToSessionState?.ToString();
            if (string.IsNullOrEmpty(expectedInResponseTo))
                throw new Saml20Exception("Expected InResponseTo not found in current session.");

            if (inResponseTo != expectedInResponseTo)
            {
                AuditLogging.logEntry(Direction.IN, Operation.LOGIN, string.Format("Unexpected value {0} for InResponseTo, expected {1}, possible replay attack!", inResponseTo, expectedInResponseTo));
                throw new Saml20Exception("Replay attack.");
            }
        }

        private static XmlDocument GetDecodedSamlResponse(HttpContext context, Encoding encoding)
        {
            string base64 = context.Request.Params["SAMLResponse"];

            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            doc.PreserveWhitespace = true;
            string samlResponse = encoding.GetString(Convert.FromBase64String(base64));
            if (Trace.ShouldTrace(TraceEventType.Information))
                Trace.TraceData(TraceEventType.Information, "Decoded SAMLResponse", samlResponse);

            doc.LoadXml(samlResponse);
            return doc;
        }

        /// <summary>
        /// Decrypts an encrypted assertion, and sends the result to the HandleAssertion method.
        /// </summary>
        private void HandleEncryptedAssertion(HttpContext context, XmlElement elem)
        {
            Trace.TraceMethodCalled(GetType(), "HandleEncryptedAssertion()");
            Saml20EncryptedAssertion decryptedAssertion = GetDecryptedAssertion(elem);
            HandleAssertion(context, decryptedAssertion.Assertion.DocumentElement);
        }

        private static Saml20EncryptedAssertion GetDecryptedAssertion(XmlElement elem)
        {
            Saml20EncryptedAssertion decryptedAssertion = new Saml20EncryptedAssertion((RSA)FederationConfig.GetConfig().SigningCertificate.GetCertificate().PrivateKey);
            decryptedAssertion.LoadXml(elem);
            decryptedAssertion.Decrypt();
            return decryptedAssertion;
        }

        /// <summary>
        /// Retrieves the name of the issuer from an XmlElement containing an assertion.
        /// </summary>
        /// <param name="assertion">An XmlElement containing an assertion</param>
        /// <returns>The identifier of the Issuer</returns>
        private string GetIssuer(XmlElement assertion)
        {
            string result = string.Empty;
            XmlNodeList list = assertion.GetElementsByTagName("Issuer", Saml20Constants.ASSERTION);
            if (list.Count > 0)
            {
                XmlElement issuer = (XmlElement) list[0];
                result = issuer.InnerText;
            }

            return result;
        }

        /// <summary>
        /// Is called before the assertion is made into a strongly typed representation
        /// </summary>
        /// <param name="context">The httpcontext.</param>
        /// <param name="elem">The assertion element.</param>
        /// <param name="endpoint">The endpoint.</param>
        protected virtual void PreHandleAssertion(HttpContext context, XmlElement elem, IDPEndPoint endpoint)
        {
            Trace.TraceMethodCalled(GetType(), "PreHandleAssertion");

            if (endpoint != null && endpoint.SLOEndpoint != null && !String.IsNullOrEmpty(endpoint.SLOEndpoint.IdpTokenAccessor))
            {
                ISaml20IdpTokenAccessor idpTokenAccessor =
                    Activator.CreateInstance(Type.GetType(endpoint.SLOEndpoint.IdpTokenAccessor, false)) as ISaml20IdpTokenAccessor;
                if (idpTokenAccessor != null)
                    idpTokenAccessor.ReadToken(elem);
            }

            Trace.TraceMethodDone(GetType(), "PreHandleAssertion");
        }

        /// <summary>
        /// Deserializes an assertion, verifies its signature and logs in the user if the assertion is valid.
        /// </summary>
        private void HandleAssertion(HttpContext context, XmlElement elem)
        {
            Trace.TraceMethodCalled(GetType(), "HandleAssertion");

            string issuer = GetIssuer(elem);
            
            IDPEndPoint endp = RetrieveIDPConfiguration(issuer);

            AuditLogging.IdpId = endp.Id;

            PreHandleAssertion(context, elem, endp);

            bool quirksMode = false;

            if (endp != null)
            {
                quirksMode = endp.QuirksMode;
            }
            
            Saml20Assertion assertion = new Saml20Assertion(elem, null, quirksMode);
            assertion.Validate(DateTime.UtcNow);

            if (endp == null || endp.metadata == null)
            {
                AuditLogging.logEntry(Direction.IN, Operation.AUTHNREQUEST_POST,
                          "Unknown login IDP, assertion: " + elem);

                HandleError(context, Resources.UnknownLoginIDP);
                return;
            }

            if (!endp.OmitAssertionSignatureCheck)
            {
                IEnumerable<string> validationFailures;
                if (!assertion.CheckSignature(GetTrustedSigners(endp.metadata.GetKeys(KeyTypes.signing), endp, out validationFailures)))
                {
                    AuditLogging.logEntry(Direction.IN, Operation.AUTHNREQUEST_POST,
                    "Invalid signature, assertion: [" + elem.OuterXml + "]");

                    string errorMessage = Resources.SignatureInvalid;

                    validationFailures = validationFailures.ToArray();
                    if (validationFailures.Any())
                    {
                        errorMessage += $"\nVerification of IDP certificate used for signature failed from the following certificate checks:\n{string.Join("\n", validationFailures)}";
                    }
                    else
                    {
                        errorMessage += $"\nVerification of IDP certificate used for signature failed with zero failures. Key(s) for signing might be missing.";
                    }

                    HandleError(context, errorMessage);
                    return;
                }
            }

            if (assertion.IsExpired())
            {
                AuditLogging.logEntry(Direction.IN, Operation.AUTHNREQUEST_POST,
                "Assertion expired, assertion: " + elem.OuterXml);

                HandleError(context, Resources.AssertionExpired);
                return;
            }

            // Only check if assertion has the required assurancelevel if it is present.
            string assuranceLevel = GetAssuranceLevel(assertion);
            string minimumAssuranceLevel = SAML20FederationConfig.GetConfig().MinimumAssuranceLevel;
            if (assuranceLevel != null)
            {
                // Assurance level is ok if the string matches the configured minimum assurance level. This is in order to support the value "Test". However, normally the value will be an integer
                if (assuranceLevel != minimumAssuranceLevel)
                {
                    // If strings are different it is still ok if the assertion has stronger assurance level than the minimum required.
                    int assuranceLevelAsInt;
                    int minimumAssuranceLevelAsInt;
                    if (!int.TryParse(assuranceLevel, out assuranceLevelAsInt) ||
                        !int.TryParse(minimumAssuranceLevel, out minimumAssuranceLevelAsInt) ||
                        assuranceLevelAsInt < minimumAssuranceLevelAsInt)
                    {
                        string errorMessage = string.Format(Resources.AssuranceLevelTooLow, assuranceLevel,
                                                            minimumAssuranceLevel);
                        AuditLogging.logEntry(Direction.IN, Operation.AUTHNREQUEST_POST,
                                              errorMessage + " Assertion: " + elem.OuterXml);

                        HandleError(context,
                                    string.Format(Resources.AssuranceLevelTooLow, assuranceLevel, minimumAssuranceLevel));
                        return;
                    }
                }
            }

            CheckConditions(context, assertion);
            AuditLogging.AssertionId = assertion.Id;
            AuditLogging.logEntry(Direction.IN, Operation.AUTHNREQUEST_POST,
                      "Assertion validated succesfully");

            DoLogin(context, assertion);
        }

        internal static IEnumerable<AsymmetricAlgorithm> GetTrustedSigners(ICollection<KeyDescriptor> keys, IDPEndPoint ep, out IEnumerable<string> validationFailureReasons)
        {
            if (keys == null)
                throw new ArgumentNullException("keys");

            var failures = new List<string>();
            List<AsymmetricAlgorithm> result = new List<AsymmetricAlgorithm>(keys.Count);
            foreach (KeyDescriptor keyDescriptor in keys)
            {
                KeyInfo ki = (KeyInfo) keyDescriptor.KeyInfo;
                    
                foreach (KeyInfoClause clause in ki)
                {
                    if(clause is KeyInfoX509Data)
                    {
                        X509Certificate2 cert = XmlSignatureUtils.GetCertificateFromKeyInfo((KeyInfoX509Data) clause);

                        string failureReason;
                        if (!IsSatisfiedByAllSpecifications(ep, cert, out failureReason))
                        {
                            failures.Add(failureReason);
                            continue;
                        }
                    }

                    AsymmetricAlgorithm key = XmlSignatureUtils.ExtractKey(clause);
                    result.Add(key);
                }
                
            }

            validationFailureReasons = failures;
            return result;
        }

        private static bool IsSatisfiedByAllSpecifications(IDPEndPoint ep, X509Certificate2 cert, out string failureReason)
        {
            foreach(ICertificateSpecification spec in SpecificationFactory.GetCertificateSpecifications(ep))
            {
                string r;
                if (!spec.IsSatisfiedBy(cert, out r))
                {
                    failureReason = $"{spec.GetType().Name}: {r}";
                    return false;

                }
            }

            failureReason = null;
            return true;
        }


        private void CheckConditions(HttpContext context, Saml20Assertion assertion)
        {
            if(assertion.IsOneTimeUse)
            {
                if (context.Cache[assertion.Id] != null)
                {
                    HandleError(context, Resources.OneTimeUseReplay);
                }
                else
                {
                    context.Cache.Insert(assertion.Id, string.Empty, null, assertion.NotOnOrAfter, Cache.NoSlidingExpiration);
                }
            }
        }

        private void DoLogin(HttpContext context, Saml20Assertion assertion)
        {
            SessionStore.AssociateUserIdWithCurrentSession(assertion.Subject.Value);

            // The assertion is what keeps the session alive. If it is ever removed ... the session will appear as removed in the SessionStoreProvider because Saml20AssertionLite is the only thing kept in session store when login flow is completed..
            SessionStore.CurrentSession[SessionConstants.Saml20AssertionLite] = Saml20AssertionLite.ToLite(assertion);
            
            if(Trace.ShouldTrace(TraceEventType.Information))
            {
                Trace.TraceData(TraceEventType.Information, string.Format(Tracing.Login, assertion.Subject.Value, assertion.SessionIndex, assertion.Subject.Format));
            }

            string assuranceLevel = GetAssuranceLevel(assertion) ?? "(Unknown)";
            
            AuditLogging.logEntry(Direction.IN, Operation.LOGIN, string.Format("Subject: {0} NameIDFormat: {1}  Level of authentication: {2}  Session timeout in minutes: {3}", assertion.Subject.Value, assertion.Subject.Format, assuranceLevel, FederationConfig.GetConfig().SessionTimeout));


            foreach(IAction action in Actions.Actions.GetActions())
            {
                Trace.TraceMethodCalled(action.GetType(), "LoginAction()");

                action.LoginAction(this, context, assertion);
                
                Trace.TraceMethodDone(action.GetType(), "LoginAction()");
            }
        }

        /// <summary>
        /// Retrieves the assurance level from the assertion.
        /// </summary>
        /// <returns>Returns the assurance level or null if it has not been defined.</returns>
        private string GetAssuranceLevel(Saml20Assertion assertion)
        {
            foreach (var attribute in assertion.Attributes)
            {
                if (attribute.Name == DKSaml20AssuranceLevelAttribute.NAME
                    && attribute.AttributeValue != null
                    && attribute.AttributeValue.Length > 0)
                    return attribute.AttributeValue[0];
            }

            return null;
        }

        private void TransferClient(IDPEndPoint idpEndpoint, Saml20AuthnRequest request, HttpContext context)
        {
            AuditLogging.AssertionId = request.ID;
            AuditLogging.IdpId = idpEndpoint.Id;

            // Determine which endpoint to use from the configuration file or the endpoint metadata.
            IDPEndPointElement destination = 
                DetermineEndpointConfiguration(SAMLBinding.REDIRECT, idpEndpoint.SSOEndpoint, idpEndpoint.metadata.SSOEndpoints());

 
    
            request.Destination = destination.Url;

            bool isPassive;
            string isPassiveAsString = context.Request.Params[IDPIsPassive];
            if (bool.TryParse(isPassiveAsString, out isPassive))
            {
                request.IsPassive = isPassive;
            }

            if (idpEndpoint.IsPassive)
                request.IsPassive = true;

            // CGI IdP fix
            if(string.IsNullOrEmpty(idpEndpoint.AssertionConsumerServiceUrl))
                request.Request.AssertionConsumerServiceURL = context.Request.Url.ToString();
            else if (Uri.IsWellFormedUriString(idpEndpoint.AssertionConsumerServiceUrl, UriKind.Absolute))
                request.Request.AssertionConsumerServiceURL = idpEndpoint.AssertionConsumerServiceUrl;
            // CGI IdP fix end

            bool forceAuthn;
            string forceAuthnAsString = context.Request.Params[IDPForceAuthn];
            if (bool.TryParse(forceAuthnAsString, out forceAuthn))
            {
                request.ForceAuthn = forceAuthn;
            }

            if (idpEndpoint.ForceAuthn)
                request.ForceAuthn = true;

            if (idpEndpoint.SSOEndpoint != null)
            {
                if (!string.IsNullOrEmpty(idpEndpoint.SSOEndpoint.ForceProtocolBinding))
                {
                    request.ProtocolBinding = idpEndpoint.SSOEndpoint.ForceProtocolBinding;
                }
            }

            //Save request message id to session
            SessionStore.CurrentSession[SessionConstants.ExpectedInResponseTo] = request.ID;

            var shaHashingAlgorithm = SignatureProviderFactory.ValidateShaHashingAlgorithm(idpEndpoint.ShaHashingAlgorithm);
            if (destination.Binding == SAMLBinding.REDIRECT)
            {
                Trace.TraceData(TraceEventType.Information, string.Format(Tracing.SendAuthnRequest, Saml20Constants.ProtocolBindings.HTTP_Redirect, idpEndpoint.Id));
                
                HttpRedirectBindingBuilder builder = new HttpRedirectBindingBuilder();
                builder.signingKey = _certificate.PrivateKey;
                builder.Request = request.GetXml().OuterXml;
                builder.ShaHashingAlgorithm = shaHashingAlgorithm;
                string s = request.Destination + "?" + builder.ToQuery();

                AuditLogging.logEntry(Direction.OUT, Operation.AUTHNREQUEST_REDIRECT, "Redirecting user to IdP for authentication", builder.Request);

                context.Response.Redirect(s, true);
                return;
            }

            if (destination.Binding == SAMLBinding.POST)
            {
                Trace.TraceData(TraceEventType.Information, string.Format(Tracing.SendAuthnRequest, Saml20Constants.ProtocolBindings.HTTP_Post, idpEndpoint.Id));

                HttpPostBindingBuilder builder = new HttpPostBindingBuilder(destination);
                //Honor the ForceProtocolBinding and only set this if it's not already set
                if (string.IsNullOrEmpty(request.ProtocolBinding))
                    request.ProtocolBinding = Saml20Constants.ProtocolBindings.HTTP_Post;
                XmlDocument req = request.GetXml();
                var signingCertificate = FederationConfig.GetConfig().SigningCertificate.GetCertificate();
                var signatureProvider = SignatureProviderFactory.CreateFromShaHashingAlgorithmName(shaHashingAlgorithm);
                signatureProvider.SignAssertion(req, request.ID, signingCertificate);
                builder.Request = req.OuterXml;
                AuditLogging.logEntry(Direction.OUT, Operation.AUTHNREQUEST_POST);

                builder.GetPage().ProcessRequest(context);
                return;
            }

            if(destination.Binding == SAMLBinding.ARTIFACT)
            {
                Trace.TraceData(TraceEventType.Information, string.Format(Tracing.SendAuthnRequest, Saml20Constants.ProtocolBindings.HTTP_Artifact, idpEndpoint.Id));

                HttpArtifactBindingBuilder builder = new HttpArtifactBindingBuilder(context);
                
                //Honor the ForceProtocolBinding and only set this if it's not already set
                if (string.IsNullOrEmpty(request.ProtocolBinding))
                    request.ProtocolBinding = Saml20Constants.ProtocolBindings.HTTP_Artifact;
                AuditLogging.logEntry(Direction.OUT, Operation.AUTHNREQUEST_REDIRECT_ARTIFACT);

                builder.RedirectFromLogin(idpEndpoint, destination, request);
            }

            HandleError(context, Resources.BindingError);
        }

    }
}
