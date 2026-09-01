using AutoMapper;
using MotoHub.DTOs;
using MotoHub.Models;

namespace MotoHub.Mapping
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<MotorcycleDTO, Motorcycle>()
                .ForMember(motorcycle => motorcycle.RetiredAtUtc, options => options.Ignore())
                .ForMember(motorcycle => motorcycle.RetirementReason, options => options.Ignore());
            CreateMap<Motorcycle, MotorcycleDTO>();
        }
    }
}
