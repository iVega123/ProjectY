using AutoMapper;
using MotoHub.DTOs;
using MotoHub.Models;

namespace MotoHub.Mapping
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<MotorcycleDTO, Motorcycle>();
            CreateMap<Motorcycle, MotorcycleDTO>();
        }
    }
}
