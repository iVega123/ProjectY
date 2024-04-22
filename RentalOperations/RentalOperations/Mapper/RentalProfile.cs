using AutoMapper;
using MongoDB.Bson;
using RentalOperations.DTOs;
using RentalOperations.Model;

namespace RentalOperations.Mapper
{
    public class RentalProfile : Profile
    {
        public RentalProfile()
        {
            CreateMap<RentalCreateDto, Rental>()
                .ForMember(dest => dest._id, opt => opt.Ignore())
                .ForMember(dest => dest.InitCost, opt => opt.Ignore());

            CreateMap<Rental, ResponseRentalDTO>()
            .ForMember(dest => dest.RentalId, opt => opt.MapFrom(src => src._id))
            .ForMember(dest => dest.MotocycleLicencePlate, opt => opt.MapFrom(src => src.MotorcycleLicencePlate))
            .ForMember(dest => dest.StartDate, opt => opt.MapFrom(src => src.StartDate))
            .ForMember(dest => dest.PredictedEndDate, opt => opt.MapFrom(src => src.PredictedEndDate))
            .ForMember(dest => dest.OriginalTotalCost, opt => opt.MapFrom(src => src.InitCost))
            .ForMember(dest => dest.ActualEndDate, opt => opt.MapFrom(src => src.EndDate))
            .ForMember(dest => dest.FinalTotalCost, opt => opt.MapFrom(src => src.FinalCost))
            .ForMember(dest => dest.AdditionalCostsOrSavings, opt => opt.MapFrom(src => src.AdditionalCostsOrSavings))
            .ForMember(dest => dest.StatusMessage, opt => opt.MapFrom(src => src.StatusMessage))
            .ForMember(dest => dest.UserId, opt => opt.MapFrom(src => src.UserId));

            CreateMap<ResponseRentalDTO, Rental>()
            .ForMember(dest => dest._id, opt => opt.MapFrom(src => new ObjectId(src.RentalId)))
            .ForMember(dest => dest.MotorcycleLicencePlate, opt => opt.MapFrom(src => src.MotocycleLicencePlate))
            .ForMember(dest => dest.StartDate, opt => opt.MapFrom(src => src.StartDate))
            .ForMember(dest => dest.EndDate, opt => opt.MapFrom(src => src.ActualEndDate))
            .ForMember(dest => dest.PredictedEndDate, opt => opt.MapFrom(src => src.PredictedEndDate))
            .ForMember(dest => dest.InitCost, opt => opt.MapFrom(src => src.OriginalTotalCost))
            .ForMember(dest => dest.FinalCost, opt => opt.MapFrom(src => src.FinalTotalCost))
            .ForMember(dest => dest.AdditionalCostsOrSavings, opt => opt.MapFrom(src => src.AdditionalCostsOrSavings))
            .ForMember(dest => dest.StatusMessage, opt => opt.MapFrom(src => src.StatusMessage))
            .ForMember(dest => dest.UserId, opt => opt.MapFrom(src => src.UserId));
        }
    }
}
