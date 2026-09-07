using AutoMapper;
using RiderManager.DTOs;
using RiderManager.Models;

namespace RiderManager.Mapping
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<RiderDTO, Rider>();
            CreateMap<Rider, RiderDTO>();

            CreateMap<Rider, RiderResponseDTO>()
            .ForMember(dest => dest.DateOfBirth, opt => opt.MapFrom(src => src.DateOfBirth))
            .ForMember(dest => dest.CNHNumber, opt => opt.MapFrom(src => src.CNHNumber))
            .ForMember(dest => dest.CNHType, opt => opt.MapFrom(src => src.CNHType))
            .ForMember(dest => dest.UserId, opt => opt.MapFrom(src => src.UserId))
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.CNPJ, opt => opt.MapFrom(src => src.CNPJ))
            .ForMember(dest => dest.CNHUrl, opt => opt.MapFrom(src => src.CNHUrl != null ? src.CNHUrl.Url : null));
        }
    }
}
