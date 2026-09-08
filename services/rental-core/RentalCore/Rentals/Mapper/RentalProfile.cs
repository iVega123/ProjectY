using AutoMapper;
using RentalOperations.DTOs;
using RentalOperations.Model;

namespace RentalOperations.Mapper
{
    public class RentalProfile : Profile
    {
        public RentalProfile()
        {
            CreateMap<Rental, ResponseRentalDTO>()
            .ForMember(dest => dest.RentalId, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.MotorcycleId, opt => opt.MapFrom(src => src.MotorcycleId))
            .ForMember(dest => dest.MotorcycleLicencePlate, opt => opt.MapFrom(src => src.MotorcycleLicencePlate))
            .ForMember(dest => dest.StartDate, opt => opt.MapFrom(src => src.StartDate))
            .ForMember(dest => dest.PredictedEndDate, opt => opt.MapFrom(src => src.PredictedEndDate))
            .ForMember(dest => dest.OriginalTotalCost, opt => opt.MapFrom(src => src.InitCost))
            .ForMember(dest => dest.ActualEndDate, opt => opt.MapFrom(src => src.EndDate))
            .ForMember(dest => dest.UserId, opt => opt.MapFrom(src => src.UserId));

        }
    }
}
