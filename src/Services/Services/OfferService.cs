namespace Microsoft.Marketplace.SaaS.SDK.Services.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Marketplace.SaaS.Models;
    using Microsoft.Marketplace.SaaS.SDK.Services.Models;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;

    /// <summary>
    /// Service to enable operations over offers.
    /// </summary>
    public class OfferService
    {
        /// <summary>
        /// The offer repository.
        /// </summary>
        private IOffersRepository offerRepository;

        /// <summary>
        /// Initializes a new instance of the <see cref="OfferService"/> class.
        /// </summary>
        /// <param name="offerRepo">The offer repo.</param>
        public OfferService(IOffersRepository offerRepo)
        {
            this.offerRepository = offerRepo;
        }

        /// <summary>
        /// Add new Offer in the database
        /// </summary>
        /// <returns>New Offer Guid</returns>
        public Guid AddOffer(string offerId, int currentUserId)
        {
            var newOfferId = this.offerRepository.Add(new Offers()
            {
                OfferId = offerId,
                OfferName = offerId,
                UserId = currentUserId,
                CreateDate = DateTime.Now,
                OfferGuid = Guid.NewGuid(),
            });

            return newOfferId;
        }

        /// <summary>
        /// Gets the offers.
        /// </summary>
        /// <returns> Offers Model.</returns>
        public List<OffersModel> GetOffers()
        {
            var allOfferData = this.offerRepository.GetAll().ToList();
            var offersList = allOfferData.Select(item => new OffersModel() {
                Id = item.Id,
                OfferID = item.OfferId,
                OfferName = item.OfferName,
                CreateDate = item.CreateDate,
                UserID = item.UserId,
                OfferGuId = item.OfferGuid
            }).ToList();

            return offersList;
        }

        /// <summary>
        /// Gets the offer on identifier.
        /// </summary>
        /// <param name="offerGuId">The offer gu identifier.</param>
        /// <returns> Offers View Model.</returns>
        public OffersViewModel GetOfferOnId(Guid offerGuId)
        {
            var offer = this.offerRepository.GetOfferById(offerGuId);
            OffersViewModel offerModel = new OffersViewModel()
            {
                Id = offer.Id,
                OfferID = offer.OfferId,
                OfferName = offer.OfferName,
                OfferGuid = offer.OfferGuid,
            };
            return offerModel;
        }
    }
}